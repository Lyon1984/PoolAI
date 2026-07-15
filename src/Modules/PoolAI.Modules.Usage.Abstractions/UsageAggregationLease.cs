namespace PoolAI.Modules.Usage.Abstractions;

public sealed record UsageAggregationLease(
    string ProjectorName,
    string PartitionKey,
    string Owner,
    long Version,
    long LastEventSequence,
    DateTimeOffset? CompletedThrough);
