namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CoordinationProbeCompleteRequest(
    EntityId AccountId,
    string Owner,
    CoordinationBreakerOutcome Outcome,
    TimeSpan? RetryAfter,
    int JitterBasisPoints,
    int SourceStatus,
    DateTimeOffset? RetryAfterAt = null);
