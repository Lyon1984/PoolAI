namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record NormalizedUpstreamResult(
    int? StatusCode,
    JsonElement Payload,
    NormalizedUpstreamUsage? Usage,
    string? ErrorCode,
    string? UpstreamRequestId = null,
    DateTimeOffset? FirstTokenAt = null)
{
    // The terminal event is held until the Gateway has finalized quota facts.
    public JsonElement? TerminalEvent { get; init; }

    // Protocol completion is held behind quota finalization, including Chat's [DONE].
    public bool StreamCompleted { get; init; }

    public override string ToString() => nameof(NormalizedUpstreamResult);
}
