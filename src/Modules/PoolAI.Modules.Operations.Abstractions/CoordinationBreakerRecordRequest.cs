namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CoordinationBreakerRecordRequest(
    EntityId AccountId,
    CoordinationBreakerOutcome Outcome,
    TimeSpan? RetryAfter,
    int JitterBasisPoints,
    int SourceStatus,
    CoordinationBreakerObservationMode ObservationMode,
    DateTimeOffset? RetryAfterAt = null);
