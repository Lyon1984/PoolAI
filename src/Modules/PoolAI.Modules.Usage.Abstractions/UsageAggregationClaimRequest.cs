namespace PoolAI.Modules.Usage.Abstractions;

public sealed record UsageAggregationClaimRequest(
    string ProjectorName,
    string PartitionKey,
    string Owner,
    TimeSpan LeaseDuration);
