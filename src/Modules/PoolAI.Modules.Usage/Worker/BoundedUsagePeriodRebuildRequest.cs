namespace PoolAI.Modules.Usage.Worker;

internal sealed record BoundedUsagePeriodRebuildRequest(
    EntityId GroupId,
    EntityId PeriodId,
    DateTimeOffset FirstBucketStart,
    DateTimeOffset LastBucketStart);
