namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record NormalizedUpstreamResult(
    int? StatusCode,
    JsonElement Payload,
    NormalizedUpstreamUsage? Usage,
    string? ErrorCode,
    string? UpstreamRequestId = null,
    DateTimeOffset? FirstTokenAt = null)
{
    public override string ToString() => nameof(NormalizedUpstreamResult);
}
