namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountHealthProbeResult(
    AccountHealthProbeOutcome Outcome,
    TimeSpan? RetryAfter,
    DateTimeOffset ObservedAt,
    int? UpstreamStatusCode = null,
    long ExpectedAccountVersion = 0,
    long ExpectedCredentialRevision = 0,
    DateTimeOffset? RetryAfterAt = null);
