using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

internal static class ResponsesResponseParser
{
    internal const int MaximumJsonBytes = 16 * 1024 * 1024;
    internal const int MaximumFrameChars = 1024 * 1024;

    internal static async ValueTask<Result<NormalizedUpstreamResult>> ParseAsync(
        AdapterUpstreamResponse response,
        string clientModel,
        bool stream,
        IGatewayResponseOutput? output,
        CancellationToken cancellationToken)
    {
        int status = response.StatusCode;
        string? upstreamRequestId = RequestId(response);
        if (status is < 200 or > 299)
        {
            string code = status switch
            {
                401 or 403 => "upstream_auth_failed",
                >= 500 or 408 => "upstream_dispatch_ambiguous",
                _ => "upstream_rejected",
            };
            return Result.Success(new NormalizedUpstreamResult(status, default, null, code, upstreamRequestId));
        }

        try
        {
            if (!response.TryGetHeader("Content-Type", out var contentTypes)
                || contentTypes.Count != 1
                || !contentTypes[0].Split(';')[0].Trim().Equals(
                    stream ? "text/event-stream" : "application/json", StringComparison.OrdinalIgnoreCase)
                || response.TryGetHeader("Content-Encoding", out var encodings)
                    && encodings.Any(static value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                return ProtocolFailure();
            }

            return stream
                ? await ParseStreamAsync(response, clientModel, output, upstreamRequestId, cancellationToken).ConfigureAwait(false)
                : await ParseJsonAsync(response.Content, clientModel, upstreamRequestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException
            or ArgumentException or InvalidOperationException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        {
            return ProtocolFailure();
        }
        catch (UpstreamReadTimeoutException exception)
        {
            return Result.Success(new NormalizedUpstreamResult(status, default, null, exception.Code, upstreamRequestId));
        }
        catch (IOException)
        {
            return Result.Success(new NormalizedUpstreamResult(status, default, null,
                stream ? "upstream_stream_error" : "upstream_dispatch_ambiguous", upstreamRequestId));
        }
    }

    private static async ValueTask<Result<NormalizedUpstreamResult>> ParseJsonAsync(
        Stream content, string clientModel, string? requestId, CancellationToken cancellationToken)
    {
        using MemoryStream bytes = new();
        byte[] block = new byte[16_384];
        while (true)
        {
            int read = await content.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bytes.Length + read > MaximumJsonBytes)
            {
                return ProtocolFailure();
            }

            bytes.Write(block, 0, read);
        }

        using JsonDocument document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)),
            new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement response = document.RootElement;
        ValidateResponse(response, expectedStatus: null);
        NormalizedUpstreamUsage? usage = ParseUsage(response);
        if (response.GetProperty("status").GetString() is "failed" or "cancelled")
        {
            return Result.Success(new NormalizedUpstreamResult(200, default, usage, "upstream_rejected", requestId));
        }
        return Result.Success(new NormalizedUpstreamResult(200,
            NormalizeResponse(response, clientModel, usage), usage,
            usage is { IsOpenAiSafeIntegerShape: false } ? "upstream_usage_out_of_range" : null, requestId));
    }

    private static async ValueTask<Result<NormalizedUpstreamResult>> ParseStreamAsync(
        AdapterUpstreamResponse upstream, string clientModel, IGatewayResponseOutput? output,
        string? requestId, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(upstream.Content, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        using CancellationTokenSource firstEvent = new(upstream.FirstEventBudget > TimeSpan.Zero
            ? upstream.FirstEventBudget : TimeSpan.Zero, upstream.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, firstEvent.Token);
        int sequence = 0;
        string? responseId = null;
        ResponsesStreamLifecycle lifecycle = new();
        try
        {
        await foreach ((string eventName, string data) in ReadFramesAsync(reader, linked.Token).ConfigureAwait(false))
        {
            using JsonDocument document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement value = document.RootElement;
            ValidateEvent(value, eventName, sequence);
            lifecycle.Accept(eventName, value);
            if (sequence == 0) { firstEvent.CancelAfter(Timeout.InfiniteTimeSpan); }
            sequence++;
            if (string.Equals(eventName, "error", StringComparison.Ordinal))
            {
                // Never forward provider messages or codes: they can contain secrets.
                return Result.Success(new NormalizedUpstreamResult(200, default, null, "upstream_stream_error", requestId));
            }

            JsonElement normalized = value.Clone();
            if (value.TryGetProperty("response", out JsonElement response))
            {
                normalized = NormalizeStreamResponse(value, response, eventName,
                    clientModel, requestId, sequence, ref responseId, out var terminal);
                if (terminal is not null) { return Result.Success(terminal); }
            }
            else if (responseId is null)
            {
                return ProtocolFailure();
            }

            if (output is not null)
            {
                await output.WriteEventAsync(eventName, normalized, cancellationToken).ConfigureAwait(false);
            }
        }

        }
        catch (OperationCanceledException exception) when (firstEvent.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new UpstreamReadTimeoutException("upstream_first_byte_timeout", exception);
        }

        return Result.Success(new NormalizedUpstreamResult(200, default, null, "upstream_stream_error", requestId));
    }

    private static JsonElement NormalizeStreamResponse(JsonElement value, JsonElement response,
        string eventName, string clientModel, string? requestId, int sequence,
        ref string? responseId, out NormalizedUpstreamResult? terminal)
    {
        bool completed = string.Equals(eventName, "response.completed", StringComparison.Ordinal);
        ValidateResponse(response, completed ? "completed" : "in_progress");
        string id = response.GetProperty("id").GetString()!;
        responseId ??= id;
        if (!string.Equals(responseId, id, StringComparison.Ordinal)
            || sequence == 1 && eventName is not "response.created"
            || sequence != 1 && eventName is "response.created"
            || eventName is "response.in_progress" && sequence != 2)
        {
            throw new JsonException("The response lifecycle is inconsistent.");
        }

        NormalizedUpstreamUsage? usage = completed ? ParseUsage(response) : null;
        if (!completed && response.GetProperty("usage").ValueKind != JsonValueKind.Null)
        {
            throw new JsonException("An in-progress response cannot carry final usage.");
        }

        JsonElement body = NormalizeResponse(response, clientModel, usage);
        JsonObject rewritten = JsonNode.Parse(value.GetRawText())!.AsObject();
        rewritten["response"] = JsonNode.Parse(body.GetRawText());
        JsonElement normalized = JsonSerializer.SerializeToElement(rewritten);
        terminal = null;
        if (completed)
        {
            string? error = usage is null ? "upstream_protocol_error"
                : !usage.IsOpenAiSafeIntegerShape ? "upstream_usage_out_of_range" : null;
            terminal = new NormalizedUpstreamResult(200, body, usage, error, requestId)
            { TerminalEvent = error is null ? normalized : null };
        }

        return normalized;
    }

    private static async IAsyncEnumerable<(string EventName, string Data)> ReadFramesAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char[] block = new char[4096];
        SseFrameAccumulator accumulator = new();
        while (true)
        {
            int read = await reader.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                accumulator.Finish();
                yield break;
            }

            for (int index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (accumulator.Consume(block[index]) is { } frame)
                {
                    yield return frame;
                }
            }
        }
    }

    private sealed class SseFrameAccumulator
    {
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private string? _eventName;
        private int _characters;

        internal (string EventName, string Data)? Consume(char character)
        {
            if (++_characters > MaximumFrameChars)
            {
                throw new JsonException("The SSE frame exceeds its safety bound.");
            }

            if (character != '\n') { _line.Append(character); return null; }
            string line = _line.ToString().TrimEnd('\r');
            _line.Clear();
            if (line.Length == 0)
            {
                _characters = 0;
                (string, string)? frame = _data.Length == 0 ? null
                    : (_eventName ?? throw new JsonException("The typed event name is missing."), _data.ToString());
                _eventName = null;
                _data.Clear();
                return frame;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                if (_eventName is not null) { throw new JsonException("The event name is repeated."); }
                _eventName = line[6..].TrimStart(' ');
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (_data.Length != 0) { _data.Append('\n'); }
                _data.Append(line.AsSpan(5).TrimStart(' '));
            }
            else if (!line.StartsWith(':')) { throw new JsonException("The SSE field is unsupported."); }
            return null;
        }

        internal void Finish()
        {
            if (_line.Length != 0 || _data.Length != 0 || _eventName is not null)
            {
                throw new JsonException("The SSE frame is incomplete.");
            }
        }
    }

    private static void ValidateResponse(JsonElement response, string? expectedStatus)
    {
        RequireObject(response);
        foreach (string field in new[] { "id", "object", "created_at", "status", "error", "incomplete_details",
            "instructions", "metadata", "model", "output", "parallel_tool_calls", "temperature", "tool_choice", "tools", "top_p" })
        {
            _ = response.GetProperty(field);
        }

        RequireString(response, "id", nonEmpty: true);
        RequireString(response, "model", nonEmpty: true);
        string? status = response.GetProperty("status").GetString();
        if (!string.Equals(response.GetProperty("object").GetString(), "response"
, StringComparison.Ordinal) || (expectedStatus is null ? status is not ("completed" or "incomplete" or "failed" or "cancelled")
                : !string.Equals(status, expectedStatus, StringComparison.Ordinal))
            || !response.GetProperty("created_at").TryGetInt64(out long createdAt) || createdAt < 0
            || (status is "failed" or "cancelled" ? response.GetProperty("error").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
                : response.GetProperty("error").ValueKind != JsonValueKind.Null)
            || response.GetProperty("incomplete_details").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
            || response.GetProperty("instructions").ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || response.GetProperty("metadata").ValueKind is not (JsonValueKind.Object or JsonValueKind.Null)
            || response.GetProperty("parallel_tool_calls").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || response.GetProperty("output").ValueKind != JsonValueKind.Array
            || response.GetProperty("tools").ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The response shape is invalid.");
        }

        ValidateResponseOptions(response);

        foreach (JsonElement item in response.GetProperty("output").EnumerateArray())
        {
            ValidateItem(item, expectedStatus is "completed" ? "completed" : null);
        }
    }

    private static void ValidateResponseOptions(JsonElement response)
    {
        JsonElement metadata = response.GetProperty("metadata");
        if (metadata.ValueKind == JsonValueKind.Object
            && metadata.EnumerateObject().Any(static property => property.Value.ValueKind != JsonValueKind.String)
            || !ResponsesProtocolAdapter.ValidToolChoice(response.GetProperty("tool_choice"))
            || response.GetProperty("tools").EnumerateArray().Any(static tool => !ResponsesProtocolAdapter.ValidTool(tool)))
        {
            throw new JsonException("The response options are invalid.");
        }

        foreach ((string name, double maximum) in new[] { ("temperature", 2d), ("top_p", 1d) })
        {
            JsonElement value = response.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.Number
                || !value.TryGetDouble(out double number) || !double.IsFinite(number) || number < 0 || number > maximum))
            {
                throw new JsonException("The response numeric option is invalid.");
            }
        }
    }

    private static void ValidateEvent(JsonElement value, string eventName, int sequence)
    {
        RequireObject(value);
        if (!string.Equals(value.GetProperty("type").GetString(), eventName
, StringComparison.Ordinal) || !value.GetProperty("sequence_number").TryGetInt32(out int number) || number != sequence)
        {
            throw new JsonException("The SSE event sequence is invalid.");
        }

        string[] fields = eventName switch
        {
            "response.created" or "response.in_progress" or "response.completed" => new[] { "response" },
            "response.output_item.added" or "response.output_item.done" => new[] { "output_index", "item" },
            "response.content_part.added" or "response.content_part.done" => new[] { "item_id", "output_index", "content_index", "part" },
            "response.output_text.delta" => new[] { "item_id", "output_index", "content_index", "delta", "logprobs" },
            "response.output_text.done" => new[] { "item_id", "output_index", "content_index", "text", "logprobs" },
            "response.function_call_arguments.delta" => new[] { "item_id", "output_index", "delta" },
            "response.function_call_arguments.done" => new[] { "item_id", "output_index", "name", "arguments" },
            "error" => new[] { "code", "message", "param" },
            _ => throw new JsonException("The event type is not supported."),
        };
        foreach (string field in fields)
        {
            JsonElement element = value.GetProperty(field);
            if (field.EndsWith("_index", StringComparison.Ordinal)
                && (!element.TryGetInt32(out int index) || index < 0))
            {
                throw new JsonException("The event index is invalid.");
            }

            if (field is "item_id" or "delta" or "text" or "name" or "arguments")
            {
                RequireString(value, field, field is "item_id" or "name");
            }
        }

        if (value.EnumerateObject().Any(p => p.Name is not ("type" or "sequence_number")
            && !fields.Contains(p.Name, StringComparer.Ordinal)))
        {
            throw new JsonException("The event has an unsupported field.");
        }

        if (value.TryGetProperty("item", out var item))
        {
            ValidateItem(item, string.Equals(eventName, "response.output_item.done", StringComparison.Ordinal) ? "completed" : "in_progress");
        }

        if (value.TryGetProperty("part", out var part))
        {
            ValidatePart(part);
        }

        if (value.TryGetProperty("logprobs", out var logprobs) && !IsObjectArray(logprobs))
        {
            throw new JsonException("The event logprobs shape is invalid.");
        }
    }

    private static void ValidateItem(JsonElement item, string? status)
    {
        RequireObject(item);
        RequireString(item, "id", true);
        string? type = item.GetProperty("type").GetString();
        if (item.GetProperty("status").GetString() is not ("in_progress" or "completed" or "incomplete"))
        {
            throw new JsonException("The output item status is unsupported.");
        }
        if (status is not null && !string.Equals(item.GetProperty("status").GetString(), status, StringComparison.Ordinal))
        {
            throw new JsonException("The output item status is invalid.");
        }

        if (string.Equals(type, "message", StringComparison.Ordinal))
        {
            if (!string.Equals(item.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal) || item.GetProperty("content").ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The message output is invalid.");
            }

            foreach (JsonElement part in item.GetProperty("content").EnumerateArray())
            {
                ValidatePart(part);
            }
        }
        else if (string.Equals(type, "function_call", StringComparison.Ordinal))
        {
            RequireString(item, "call_id", true);
            RequireString(item, "name", true);
            RequireString(item, "arguments", false);
            if (item.GetProperty("name").GetString()!.Length > 64
                || item.EnumerateObject().Any(static p => p.Name is not ("id" or "type" or "status" or "arguments" or "call_id" or "name")))
            {
                throw new JsonException("The function output shape is invalid.");
            }
        }
        else
        {
            throw new JsonException("The output item is unsupported.");
        }
    }

    private static void ValidatePart(JsonElement part)
    {
        RequireObject(part);
        if (!string.Equals(part.GetProperty("type").GetString(), "output_text"
, StringComparison.Ordinal) || !IsObjectArray(part.GetProperty("annotations"))
            || !IsObjectArray(part.GetProperty("logprobs")))
        {
            throw new JsonException("The output content is unsupported.");
        }

        RequireString(part, "text", false);
    }

    private static bool IsObjectArray(JsonElement value) => value.ValueKind == JsonValueKind.Array
        && value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.Object);

    private static NormalizedUpstreamUsage? ParseUsage(JsonElement response)
    {
        if (!response.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireObject(usage);
        BigInteger input = Integer(usage, "input_tokens");
        BigInteger output = Integer(usage, "output_tokens");
        if (Integer(usage, "total_tokens") != input + output)
        {
            throw new JsonException("The usage total is inconsistent.");
        }

        BigInteger cached = Detail(usage, "input_tokens_details", "cached_tokens");
        BigInteger written = Detail(usage, "input_tokens_details", "cache_write_tokens");
        BigInteger thinking = Detail(usage, "output_tokens_details", "reasoning_tokens");
        JsonElement evidence = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["input_tokens"] = usage.GetProperty("input_tokens").Clone(),
            ["output_tokens"] = usage.GetProperty("output_tokens").Clone(),
            ["total_tokens"] = usage.GetProperty("total_tokens").Clone(),
            ["input_tokens_details"] = CounterObject(("cached_tokens", cached), ("cache_write_tokens", written)),
            ["output_tokens_details"] = CounterObject(("reasoning_tokens", thinking)),
        });
        return new NormalizedUpstreamUsage(input, output, cached, written, thinking, evidence);
    }

    private static BigInteger Detail(JsonElement usage, string name, string counter) =>
        usage.TryGetProperty(name, out var details) && details.ValueKind != JsonValueKind.Null
            ? details.TryGetProperty(counter, out _) ? Integer(details, counter) : BigInteger.Zero
            : BigInteger.Zero;

    private static BigInteger Integer(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        string raw = field.GetRawText();
        if (field.ValueKind != JsonValueKind.Number || raw.Length > 4096
            || raw.Length == 0 || raw.Any(static character => character is < '0' or > '9'))
        {
            throw new JsonException("The usage counter is not an exact non-negative integer.");
        }

        return BigInteger.Parse(raw, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static JsonElement CounterObject(params (string Name, BigInteger Value)[] values)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            foreach ((string name, BigInteger value) in values)
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement NormalizeResponse(JsonElement response, string model, NormalizedUpstreamUsage? usage)
    {
        JsonObject normalized = JsonNode.Parse(response.GetRawText())!.AsObject();
        normalized["model"] = model;
        normalized["usage"] = usage is null ? null : JsonNode.Parse(usage.RawEvidence!.Value.GetRawText());
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static string? RequestId(AdapterUpstreamResponse response) =>
        response.TryGetHeader("x-request-id", out var values) && values.Count == 1
        && values[0] is { Length: >= 1 and <= 200 } value
        && value.StartsWith("req_", StringComparison.Ordinal)
        && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? value : null;

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || ResponsesProtocolAdapter.HasDuplicateNames(value))
        {
            throw new JsonException("The object is invalid.");
        }
    }

    private static void RequireString(JsonElement value, string name, bool nonEmpty)
    {
        if (!ResponsesProtocolAdapter.String(value, name, out string? text) || nonEmpty && string.IsNullOrEmpty(text))
        {
            throw new JsonException("A response string is invalid.");
        }
    }

    private static Result<NormalizedUpstreamResult> ProtocolFailure() => Result.Failure<NormalizedUpstreamResult>(
        "upstream_protocol_error", "The upstream response does not satisfy the frozen Responses protocol.");
}
