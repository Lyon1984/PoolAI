using System.Text.Json;
using System.Numerics;
using System.Globalization;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

public sealed class ResponsesProtocolAdapter : IProtocolAdapter
{
    private static readonly IReadOnlyList<string> FieldError =
        Array.AsReadOnly(new[] { "The field does not satisfy the Release 1 contract." });
    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
    {
        "model", "input", "instructions", "previous_response_id", "stream",
        "max_output_tokens", "temperature", "top_p", "tools", "tool_choice",
        "metadata", "include",
    };

    public AdapterCapability Capability => OpenAiCapabilityDescriptor.R1Capabilities[0];

    public ValueTask<Result<NormalizedGatewayRequest>> NormalizeAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Normalize(request));
    }

    private static Result<NormalizedGatewayRequest> Normalize(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || HasDuplicateNames(request))
        {
            return Failure("invalid_request", "The request JSON is invalid.");
        }

        foreach (JsonProperty field in request.EnumerateObject())
        {
            if (!SupportedFields.Contains(field.Name))
            {
                return Failure("unsupported_feature", "The request uses a feature outside Release 1.");
            }
        }

        if (!String(request, "model", out string? model)
            || model is not { Length: >= 1 and <= 200 }
            || !string.Equals(model, model.Trim(), StringComparison.Ordinal))
        {
            return Invalid("/model");
        }

        if (!request.TryGetProperty("input", out JsonElement input)
            || !ValidInput(input))
        {
            return Invalid("/input");
        }

        bool stream = false;
        if (request.TryGetProperty("stream", out JsonElement streamValue))
        {
            if (streamValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return Invalid("/stream");
            }

            stream = streamValue.GetBoolean();
        }

        return ValidateTextAndLimits(request) ?? ValidateToolsAndMetadata(request)
            ?? Result.Success(new NormalizedGatewayRequest(EntityId.New(), model, stream, request.Clone()));
    }

    private static Result<NormalizedGatewayRequest>? ValidateTextAndLimits(JsonElement request)
    {
        foreach (string name in new[] { "instructions", "previous_response_id" })
        {
            if (request.TryGetProperty(name, out JsonElement value)
                && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return Invalid("/" + name);
            }
        }

        if (request.TryGetProperty("previous_response_id", out JsonElement previous)
            && previous.ValueKind == JsonValueKind.String
            && (previous.GetString() is not { Length: >= 1 and <= 200 } id
                || id.Any(static character => character is < '!' or > '~')))
        {
            return Invalid("/previous_response_id");
        }

        if (request.TryGetProperty("max_output_tokens", out JsonElement maximum)
            && !IsPositiveSafeInteger(maximum))
        {
            return Invalid("/max_output_tokens");
        }

        foreach ((string name, double max) in new[] { ("temperature", 2d), ("top_p", 1d) })
        {
            if (request.TryGetProperty(name, out JsonElement value)
                && (value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDouble(out double number)
                    || !double.IsFinite(number) || number < 0 || number > max))
            {
                return Invalid("/" + name);
            }
        }

        return null;
    }

    private static Result<NormalizedGatewayRequest>? ValidateToolsAndMetadata(JsonElement request)
    {
        if (request.TryGetProperty("tools", out JsonElement tools)
            && (tools.ValueKind != JsonValueKind.Array
                || tools.EnumerateArray().Any(static tool => !ValidTool(tool))))
        {
            return Failure("unsupported_feature", "Only Release 1 function tools are supported.");
        }

        if (request.TryGetProperty("tool_choice", out JsonElement choice)
            && !ValidToolChoice(choice))
        {
            return Invalid("/tool_choice");
        }

        if (request.TryGetProperty("metadata", out JsonElement metadata)
            && (metadata.ValueKind != JsonValueKind.Object
                || metadata.EnumerateObject().Any(static p => p.Value.ValueKind != JsonValueKind.String)))
        {
            return Invalid("/metadata");
        }

        if (request.TryGetProperty("include", out JsonElement include)
            && (include.ValueKind != JsonValueKind.Array || include.GetArrayLength() != 0))
        {
            return Failure("unsupported_feature", "Response includes are not supported in Release 1.");
        }

        return null;
    }

    internal static bool HasDuplicateNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateNames(property.Value))
                {
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Any(HasDuplicateNames);
        }

        return false;
    }

    private static bool ValidInput(JsonElement input) => input.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Array => input.GetArrayLength() > 0
            && input.EnumerateArray().All(ValidInputItem),
        _ => false,
    };

    private static bool ValidInputItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? type = item.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String
            ? kind.GetString() : "message";
        if (item.TryGetProperty("type", out kind) && kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        return type switch
        {
            "message" => Only(item, "type", "role", "content")
                && String(item, "role", out string? role)
                && role is "system" or "developer" or "user" or "assistant"
                && item.TryGetProperty("content", out var content)
                && (content.ValueKind == JsonValueKind.String
                    || content.ValueKind == JsonValueKind.Array
                        && content.GetArrayLength() > 0
                        && content.EnumerateArray().All(static part => Only(part, "type", "text")
                            && String(part, "type", out var partType) && string.Equals(partType, "input_text"
, StringComparison.Ordinal) && String(part, "text", out _))),
            "function_call" => String(item, "call_id", out _)
                && String(item, "name", out var name) && name is { Length: >= 1 and <= 64 }
                && String(item, "arguments", out _)
                && (!item.TryGetProperty("id", out var id) || id.ValueKind == JsonValueKind.String)
                && (!item.TryGetProperty("status", out var status) || status.ValueKind == JsonValueKind.String
                    && status.GetString() is "in_progress" or "completed" or "incomplete"),
            "function_call_output" => Only(item, "type", "call_id", "output")
                && String(item, "call_id", out _) && String(item, "output", out _),
            _ => false,
        };
    }

    internal static bool ValidTool(JsonElement tool) => Only(tool, "type", "name", "description", "parameters", "strict")
        && String(tool, "type", out var type) && string.Equals(type, "function"
, StringComparison.Ordinal) && String(tool, "name", out var name) && name is { Length: >= 1 and <= 64 }
        && tool.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Object
        && (!tool.TryGetProperty("description", out var description) || description.ValueKind == JsonValueKind.String)
        && (!tool.TryGetProperty("strict", out var strict) || strict.ValueKind is JsonValueKind.True or JsonValueKind.False);

    internal static bool ValidToolChoice(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is "none" or "auto" or "required",
        JsonValueKind.Object => Only(value, "type", "name") && String(value, "type", out var type)
            && string.Equals(type, "function", StringComparison.Ordinal) && String(value, "name", out var name) && name is { Length: >= 1 and <= 64 },
        _ => false,
    };

    private static bool Only(JsonElement value, params string[] fields) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal));

    internal static bool IsPositiveSafeInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number) { return false; }
        string raw = value.GetRawText();
        if (raw.Length > 64 || raw.StartsWith('-')) { return false; }
        int exponentIndex = raw.IndexOfAny(['e', 'E']);
        int exponent = 0;
        if (exponentIndex >= 0)
        {
            if (!int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out exponent)) { return false; }
            raw = raw[..exponentIndex];
        }

        int decimalIndex = raw.IndexOf('.');
        int fractional = decimalIndex >= 0 ? raw.Length - decimalIndex - 1 : 0;
        BigInteger number = BigInteger.Parse(raw.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        long scale = (long)exponent - fractional;
        if (scale > 16 || scale < -64) { return false; }
        if (scale >= 0) { number *= BigInteger.Pow(10, checked((int)scale)); }
        else
        {
            number = BigInteger.DivRem(number, BigInteger.Pow(10, checked((int)-scale)), out BigInteger remainder);
            if (remainder != BigInteger.Zero) { return false; }
        }

        return number >= BigInteger.One && number <= new BigInteger(9_007_199_254_740_991L);
    }

    internal static bool String(JsonElement value, string name, out string? text)
    {
        text = null;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var field)
            || field.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = field.GetString();
        return true;
    }

    private static Result<NormalizedGatewayRequest> Invalid(string pointer) => Result.Failure<NormalizedGatewayRequest>(
        "validation_failed", "The request fields are invalid.", presentation: new ResultErrorPresentation(
            "validation_failed", 422, "Validation failed", "The request fields are invalid.", false,
            Errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            { [pointer] = FieldError }));

    private static Result<NormalizedGatewayRequest> Failure(string code, string message) =>
        Result.Failure<NormalizedGatewayRequest>(code, message);
}
