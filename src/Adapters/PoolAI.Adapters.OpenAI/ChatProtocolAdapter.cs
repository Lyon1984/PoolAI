using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

/// <summary>The frozen R1 text/function subset; tools are data, never executed here.</summary>
public sealed class ChatProtocolAdapter : IProtocolAdapter
{
    private static readonly IReadOnlyList<string> FieldError =
        Array.AsReadOnly(new[] { "The field does not satisfy the Release 1 contract." });
    private static readonly HashSet<string> SupportedFields = new(StringComparer.Ordinal)
    {
        "model", "messages", "stream", "max_completion_tokens", "temperature", "top_p",
        "tools", "tool_choice", "stream_options",
    };

    public AdapterCapability Capability => OpenAiCapabilityDescriptor.R1Capabilities[2];

    public ValueTask<Result<NormalizedGatewayRequest>> NormalizeAsync(
        JsonElement request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Normalize(request));
    }

    private static Result<NormalizedGatewayRequest> Normalize(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || ResponsesProtocolAdapter.HasDuplicateNames(request))
        {
            return Result.Failure<NormalizedGatewayRequest>("invalid_request", "The request JSON is invalid.");
        }

        if (request.EnumerateObject().Any(static field => !SupportedFields.Contains(field.Name)))
        {
            return Unsupported();
        }

        if (!Text(request, "model", out string? model) || !WithinLength(model, 1, 200))
        {
            return Invalid("/model");
        }
        if (!request.TryGetProperty("messages", out JsonElement messages)
            || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0
            || messages.EnumerateArray().Any(static message => !ValidMessage(message)))
        {
            return Invalid("/messages");
        }

        bool stream = false;
        if (request.TryGetProperty("stream", out JsonElement mode))
        {
            if (!Boolean(mode)) { return Invalid("/stream"); }
            stream = mode.GetBoolean();
        }
        if (request.TryGetProperty("max_completion_tokens", out JsonElement maximum)
            && !ResponsesProtocolAdapter.IsPositiveSafeInteger(maximum))
        {
            return Invalid("/max_completion_tokens");
        }
        foreach ((string name, double maximumValue) in new[] { ("temperature", 2d), ("top_p", 1d) })
        {
            if (request.TryGetProperty(name, out JsonElement value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number)
                    || !double.IsFinite(number) || number < 0 || number > maximumValue))
            {
                return Invalid("/" + name);
            }
        }
        if (request.TryGetProperty("tools", out JsonElement tools)
            && (tools.ValueKind != JsonValueKind.Array || tools.EnumerateArray().Any(static tool => !ValidTool(tool))))
        {
            return Unsupported();
        }
        if (request.TryGetProperty("tool_choice", out JsonElement choice) && !ValidToolChoice(choice))
        {
            return Invalid("/tool_choice");
        }
        if (request.TryGetProperty("stream_options", out JsonElement options)
            && (!Only(options, "include_usage", "include_obfuscation")
                || options.EnumerateObject().Any(static property => !Boolean(property.Value))))
        {
            return Invalid("/stream_options");
        }

        return Result.Success(new NormalizedGatewayRequest(EntityId.New(), model!, stream, request.Clone()));
    }

    internal static bool ValidMessage(JsonElement message)
    {
        if (!Text(message, "role", out string? role)) { return false; }
        return role switch
        {
            "system" or "developer" or "user" => Only(message, "role", "content", "name")
                && message.TryGetProperty("content", out JsonElement content) && ValidContent(content)
                && OptionalText(message, "name"),
            "tool" => Only(message, "role", "tool_call_id", "content")
                && Text(message, "tool_call_id", out _)
                && message.TryGetProperty("content", out JsonElement content) && ValidContent(content),
            "assistant" => ValidAssistant(message),
            _ => false,
        };
    }

    private static bool ValidAssistant(JsonElement message)
    {
        if (!Only(message, "role", "content", "tool_calls", "refusal", "name") || !OptionalText(message, "name"))
        {
            return false;
        }
        bool hasContent = message.TryGetProperty("content", out JsonElement content);
        bool hasTools = message.TryGetProperty("tool_calls", out JsonElement tools);
        return (!hasContent || content.ValueKind == JsonValueKind.Null || ValidContent(content))
            && (!hasTools || tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0
                && tools.EnumerateArray().All(ValidToolCall))
            && (hasContent && ValidContent(content) || hasTools)
            && (!message.TryGetProperty("refusal", out JsonElement refusal)
                || refusal.ValueKind is JsonValueKind.String or JsonValueKind.Null);
    }

    internal static bool ValidToolCall(JsonElement call) => Only(call, "id", "type", "function")
        && Text(call, "id", out _) && Text(call, "type", out string? type) && string.Equals(type, "function", StringComparison.Ordinal)
        && call.TryGetProperty("function", out JsonElement function) && Only(function, "name", "arguments")
        && Text(function, "name", out _) && Text(function, "arguments", out _);

    private static bool ValidContent(JsonElement content) => content.ValueKind == JsonValueKind.String
        || content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            && content.EnumerateArray().All(static part => Only(part, "type", "text")
                && Text(part, "type", out string? type) && string.Equals(type, "text", StringComparison.Ordinal) && Text(part, "text", out _));

    private static bool ValidTool(JsonElement tool) => Only(tool, "type", "function")
        && Text(tool, "type", out string? type) && string.Equals(type, "function", StringComparison.Ordinal)
        && tool.TryGetProperty("function", out JsonElement function)
        && Only(function, "name", "description", "parameters", "strict")
        && Text(function, "name", out string? name) && WithinLength(name, 1, 64)
        && OptionalText(function, "description")
        && function.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object
        && (!function.TryGetProperty("strict", out JsonElement strict) || Boolean(strict));

    private static bool ValidToolChoice(JsonElement choice) => choice.ValueKind == JsonValueKind.String
        ? choice.GetString() is "none" or "auto" or "required"
        : Only(choice, "type", "function") && Text(choice, "type", out string? type) && string.Equals(type, "function", StringComparison.Ordinal)
            && choice.TryGetProperty("function", out JsonElement function) && Only(function, "name")
            && Text(function, "name", out _);

    internal static bool Only(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().All(property => names.Contains(property.Name, StringComparer.Ordinal));
    internal static bool Text(JsonElement value, string name, out string? text) =>
        ResponsesProtocolAdapter.String(value, name, out text);
    internal static bool WithinLength(string? value, int minimum, int maximum)
    {
        if (value is null) { return false; }
        int length = 0;
        foreach (Rune _ in value.EnumerateRunes())
        {
            if (++length > maximum) { return false; }
        }
        return length >= minimum;
    }
    private static bool OptionalText(JsonElement value, string name) =>
        !value.TryGetProperty(name, out JsonElement field) || field.ValueKind == JsonValueKind.String;
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static Result<NormalizedGatewayRequest> Unsupported() =>
        Result.Failure<NormalizedGatewayRequest>("unsupported_feature", "Only Release 1 text and function features are supported.");
    private static Result<NormalizedGatewayRequest> Invalid(string pointer) =>
        Result.Failure<NormalizedGatewayRequest>("validation_failed", "The request does not satisfy the Release 1 contract.",
            presentation: new ResultErrorPresentation("validation_failed", 422, "Request validation failed",
                "The request does not satisfy the Release 1 contract.", false,
                Errors: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [pointer] = FieldError }));
}
