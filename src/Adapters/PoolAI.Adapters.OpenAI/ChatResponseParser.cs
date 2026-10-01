using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

internal static class ChatResponseParser
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const int MaximumFrameChars = 1024 * 1024;
    private static readonly BigInteger MaximumSafeInteger = new(9_007_199_254_740_991L);

    internal static async ValueTask<Result<NormalizedUpstreamResult>> ParseAsync(
        AdapterUpstreamResponse response, string clientModel, bool stream, bool includeUsage,
        bool includeObfuscation, IGatewayResponseOutput? output, CancellationToken cancellationToken)
    {
        string? requestId = response.TryGetHeader("x-request-id", out var ids) && ids.Count == 1
            && ids[0] is { Length: >= 1 and <= 200 } id && id.All(static c => c is >= '!' and <= '~') ? ids[0] : null;
        if (response.StatusCode is < 200 or > 299)
        {
            string code = response.StatusCode switch
            {
                401 or 403 => "upstream_auth_failed",
                >= 500 or 408 => "upstream_dispatch_ambiguous",
                _ => "upstream_rejected",
            };
            return Result.Success(new NormalizedUpstreamResult(response.StatusCode, default, null, code, requestId));
        }
        try
        {
            if (!response.TryGetHeader("Content-Type", out var types) || types.Count != 1
                || !types[0].Split(';')[0].Trim().Equals(stream ? "text/event-stream" : "application/json", StringComparison.OrdinalIgnoreCase)
                || response.TryGetHeader("Content-Encoding", out var encodings)
                    && encodings.Any(static encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            {
                return ProtocolFailure();
            }
            return stream
                ? await ParseStreamAsync(response, clientModel, includeUsage, includeObfuscation, output, requestId, cancellationToken).ConfigureAwait(false)
                : await ParseJsonAsync(response.Content, clientModel, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UpstreamReadTimeoutException exception)
        {
            return Result.Success(new NormalizedUpstreamResult(200, default, null, exception.Code, requestId));
        }
        catch (IOException)
        {
            return Result.Success(new NormalizedUpstreamResult(200, default, null,
                stream ? "upstream_stream_error" : "upstream_dispatch_ambiguous", requestId));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException
            or InvalidOperationException or OverflowException or DecoderFallbackException or KeyNotFoundException)
        {
            return ProtocolFailure();
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
            if (read == 0) { break; }
            if (bytes.Length + read > MaximumJsonBytes) { return ProtocolFailure(); }
            bytes.Write(block, 0, read);
        }
        using JsonDocument document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)),
            new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement value = document.RootElement;
        ValidateIdentity(value, "chat.completion");
        JsonElement choices = value.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array) { throw new JsonException(); }
        HashSet<int> indices = [];
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (choice.ValueKind != JsonValueKind.Object || !indices.Add(Index(choice))
                || !choice.TryGetProperty("message", out JsonElement message) || !ChatProtocolAdapter.ValidMessage(message)
                || !string.Equals(message.GetProperty("role").GetString(), "assistant", StringComparison.Ordinal)
                || choice.GetProperty("finish_reason").ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw new JsonException("The Chat completion choice is invalid.");
            }
        }
        NormalizedUpstreamUsage? usage = ParseUsage(value, out bool safe);
        JsonElement normalized = Normalize(value, clientModel, stream: false, includeUsage: true, includeObfuscation: true);
        return Result.Success(new NormalizedUpstreamResult(200, normalized, usage,
            safe ? null : "upstream_usage_out_of_range", requestId));
    }

    private static async ValueTask<Result<NormalizedUpstreamResult>> ParseStreamAsync(
        AdapterUpstreamResponse response, string clientModel, bool includeUsage, bool includeObfuscation,
        IGatewayResponseOutput? output, string? requestId, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(response.Content, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
        using CancellationTokenSource firstEvent = new(response.FirstEventBudget > TimeSpan.Zero
            ? response.FirstEventBudget : TimeSpan.Zero, response.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, firstEvent.Token);
        StreamState state = new();
        try
        {
            await foreach (string data in ReadFramesAsync(reader, linked.Token).ConfigureAwait(false))
            {
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    state.Lifecycle.Complete(includeUsage);
                    return Result.Success(new NormalizedUpstreamResult(200, default, state.Usage, null, requestId)
                    { StreamCompleted = true, TerminalEvent = state.Terminal });
                }
                using JsonDocument document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 64 });
                JsonElement value = document.RootElement;
                if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("error", out _))
                {
                    // Provider strings are never a public error projection.
                    return StreamFailure(state, "upstream_stream_error", requestId);
                }
                ValidateIdentity(value, "chat.completion.chunk");
                bool usageChunk = state.Lifecycle.Accept(value);
                firstEvent.CancelAfter(Timeout.InfiniteTimeSpan);
                bool safe = await ProcessChunkAsync(value, usageChunk, state, clientModel, includeUsage,
                    includeObfuscation, output, cancellationToken).ConfigureAwait(false);
                if (!safe) { return StreamFailure(state, "upstream_usage_out_of_range", requestId); }
            }
        }
        catch (OperationCanceledException) when (firstEvent.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return StreamFailure(state, "upstream_first_byte_timeout", requestId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or OverflowException or KeyNotFoundException or FormatException or DecoderFallbackException)
        {
            return StreamFailure(state, "upstream_protocol_error", requestId);
        }
        catch (UpstreamReadTimeoutException exception)
        {
            return StreamFailure(state, exception.Code, requestId);
        }
        catch (IOException)
        {
            return StreamFailure(state, "upstream_stream_error", requestId);
        }
        return StreamFailure(state, "upstream_stream_error", requestId);
    }

    private static async ValueTask<bool> ProcessChunkAsync(JsonElement value, bool usageChunk, StreamState state,
        string clientModel, bool includeUsage, bool includeObfuscation, IGatewayResponseOutput? output,
        CancellationToken cancellationToken)
    {
        if (usageChunk)
        {
            state.Usage = ParseUsage(value, out bool safe);
            if (!safe) { return false; }
            if (includeUsage) { state.Terminal = Normalize(value, clientModel, true, true, includeObfuscation); }
        }
        else if (output is not null)
        {
            await output.WriteEventAsync("chat.completion.chunk",
                Normalize(value, clientModel, true, includeUsage, includeObfuscation), cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private static Result<NormalizedUpstreamResult> StreamFailure(StreamState state, string code, string? requestId) =>
        Result.Success(new NormalizedUpstreamResult(200, default, state.Usage, code, requestId));

    private sealed class StreamState
    {
        internal ChatStreamLifecycle Lifecycle { get; } = new();
        internal NormalizedUpstreamUsage? Usage { get; set; }
        internal JsonElement? Terminal { get; set; }
    }

    internal static void ValidateIdentity(JsonElement value, string kind)
    {
        if (value.ValueKind != JsonValueKind.Object || ResponsesProtocolAdapter.HasDuplicateNames(value)
            || !ChatProtocolAdapter.Text(value, "id", out string? id) || string.IsNullOrEmpty(id)
            || !ChatProtocolAdapter.Text(value, "object", out string? type) || !string.Equals(type, kind, StringComparison.Ordinal)
            || !ChatProtocolAdapter.Text(value, "model", out string? model) || string.IsNullOrEmpty(model)
            || !value.GetProperty("created").TryGetInt64(out _))
        {
            throw new JsonException("The Chat completion identity is invalid.");
        }
    }

    internal static int Index(JsonElement value)
    {
        if (!value.GetProperty("index").TryGetInt32(out int index) || index < 0) { throw new JsonException(); }
        return index;
    }

    private static NormalizedUpstreamUsage? ParseUsage(JsonElement value, out bool safe)
    {
        safe = true;
        if (!value.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind == JsonValueKind.Null) { return null; }
        if (usage.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
        BigInteger input = Integer(usage, "prompt_tokens");
        BigInteger output = Integer(usage, "completion_tokens");
        if (Integer(usage, "total_tokens") != input + output) { throw new JsonException("The usage total is inconsistent."); }
        BigInteger cached = Detail(usage, "prompt_tokens_details", "cached_tokens");
        BigInteger written = Detail(usage, "prompt_tokens_details", "cache_write_tokens");
        BigInteger thinking = Detail(usage, "completion_tokens_details", "reasoning_tokens");
        BigInteger accepted = Detail(usage, "completion_tokens_details", "accepted_prediction_tokens");
        BigInteger rejected = Detail(usage, "completion_tokens_details", "rejected_prediction_tokens");
        JsonElement evidence = NormalizeUsage(usage);
        NormalizedUpstreamUsage result = new(input, output, cached, written, thinking, evidence);
        safe = result.IsOpenAiSafeIntegerShape && accepted <= MaximumSafeInteger && rejected <= MaximumSafeInteger;
        return result;
    }

    private static BigInteger Detail(JsonElement usage, string name, string counter)
    {
        if (!usage.TryGetProperty(name, out JsonElement detail)) { return BigInteger.Zero; }
        if (detail.ValueKind != JsonValueKind.Object) { throw new JsonException(); }
        return detail.TryGetProperty(counter, out _) ? Integer(detail, counter) : BigInteger.Zero;
    }

    private static BigInteger Integer(JsonElement value, string name)
    {
        JsonElement field = value.GetProperty(name);
        string raw = field.GetRawText();
        if (field.ValueKind != JsonValueKind.Number || raw.Length is 0 or > 4096
            || raw.Any(static character => character is < '0' or > '9')) { throw new JsonException(); }
        return BigInteger.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static JsonElement NormalizeUsage(JsonElement usage)
    {
        JsonObject normalized = new();
        Copy(usage, normalized, "prompt_tokens", "completion_tokens", "total_tokens");
        foreach ((string name, string[] counters) in new[]
        {
            ("prompt_tokens_details", new[] { "cached_tokens", "cache_write_tokens" }),
            ("completion_tokens_details", new[] { "reasoning_tokens", "accepted_prediction_tokens", "rejected_prediction_tokens" }),
        })
        {
            if (usage.TryGetProperty(name, out JsonElement details))
            {
                JsonObject item = new();
                Copy(details, item, counters);
                normalized[name] = item;
            }
        }
        return JsonSerializer.SerializeToElement(normalized);
    }

    private static JsonElement Normalize(JsonElement value, string model, bool stream, bool includeUsage, bool includeObfuscation)
    {
        JsonObject result = new();
        Copy(value, result, "id", "object", "created", "choices");
        result["model"] = model;
        if (stream)
        {
            JsonArray choices = result["choices"]!.AsArray();
            foreach (JsonNode? node in choices)
            {
                JsonObject choice = node!.AsObject();
                foreach (string name in choice.Select(static field => field.Key).ToArray())
                {
                    if (name is not ("index" or "delta" or "finish_reason")) { choice.Remove(name); }
                }
            }
            if (choices.Count == 0 && value.TryGetProperty("usage", out JsonElement usage))
            {
                result["usage"] = JsonNode.Parse(NormalizeUsage(usage).GetRawText());
            }
            else if (includeUsage || value.TryGetProperty("usage", out _)) { result["usage"] = null; }
            if (includeObfuscation && value.TryGetProperty("obfuscation", out JsonElement padding))
            {
                if (padding.ValueKind != JsonValueKind.String || padding.GetString() is not { Length: > 0 }) { throw new JsonException(); }
                result["obfuscation"] = padding.GetString();
            }
        }
        else if (value.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind != JsonValueKind.Null)
        {
            result["usage"] = JsonNode.Parse(NormalizeUsage(usage).GetRawText());
        }
        return JsonSerializer.SerializeToElement(result);
    }

    private static void Copy(JsonElement source, JsonObject target, params string[] fields)
    {
        foreach (string name in fields)
        {
            if (source.TryGetProperty(name, out JsonElement value)) { target[name] = JsonNode.Parse(value.GetRawText()); }
        }
    }

    private static async IAsyncEnumerable<string> ReadFramesAsync(StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        char[] block = new char[4096];
        StringBuilder line = new();
        StringBuilder data = new();
        bool hasData = false;
        int characters = 0;
        while (true)
        {
            int read = await reader.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (line.Length != 0 || hasData) { throw new JsonException("An incomplete SSE frame remains."); }
                yield break;
            }
            for (int index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++characters > MaximumFrameChars) { throw new JsonException("The SSE frame exceeds its safety bound."); }
                char character = block[index];
                if (character != '\n') { line.Append(character); continue; }
                string text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    characters = 0;
                    if (hasData) { yield return data.ToString(); }
                    data.Clear();
                    hasData = false;
                }
                else if (text.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (hasData) { data.Append('\n'); }
                    ReadOnlySpan<char> field = text.AsSpan(5);
                    if (field.StartsWith(" ", StringComparison.Ordinal)) { field = field[1..]; }
                    data.Append(field);
                    hasData = true;
                }
                else if (!text.StartsWith(':')) { throw new JsonException("The Chat SSE field is unsupported."); }
            }
        }
    }

    private static Result<NormalizedUpstreamResult> ProtocolFailure() =>
        Result.Failure<NormalizedUpstreamResult>("upstream_protocol_error", "The upstream Chat protocol is invalid.");
}
