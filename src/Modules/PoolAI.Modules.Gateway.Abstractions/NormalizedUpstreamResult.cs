namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record NormalizedUpstreamResult(
    bool WasDispatched,
    bool RequestBytesWritten,
    int? StatusCode,
    JsonElement Payload,
    long? InputTokens,
    long? OutputTokens,
    string? ErrorCode);
