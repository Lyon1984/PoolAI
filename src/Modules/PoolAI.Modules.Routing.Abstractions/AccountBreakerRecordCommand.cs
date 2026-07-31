namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountBreakerRecordCommand(
    EntityId AccountId,
    AccountBreakerOutcome Outcome,
    TimeSpan? RetryAfter = null,
    int? UpstreamStatusCode = null,
    AccountBreakerObservationMode ObservationMode =
        AccountBreakerObservationMode.Passive,
    DateTimeOffset? ObservedAt = null,
    long ExpectedAccountVersion = 0,
    long ExpectedCredentialRevision = 0,
    DateTimeOffset? RetryAfterAt = null);
