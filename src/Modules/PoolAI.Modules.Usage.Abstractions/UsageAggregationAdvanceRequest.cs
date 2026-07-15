namespace PoolAI.Modules.Usage.Abstractions;

public sealed record UsageAggregationAdvanceRequest(
    UsageAggregationLease Lease,
    long NextEventSequence,
    DateTimeOffset? CompletedThrough);
