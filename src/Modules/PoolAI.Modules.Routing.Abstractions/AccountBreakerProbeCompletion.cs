namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountBreakerProbeCompletion(
    AccountBreakerOutcome Outcome,
    TimeSpan? RetryAfter = null,
    int? UpstreamStatusCode = null,
    DateTimeOffset? ObservedAt = null,
    long ExpectedAccountVersion = 0,
    long ExpectedCredentialRevision = 0,
    DateTimeOffset? RetryAfterAt = null);
