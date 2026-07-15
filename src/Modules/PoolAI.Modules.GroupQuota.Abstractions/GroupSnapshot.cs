namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupSnapshot(
    EntityId GroupId,
    GroupLifecycle Lifecycle,
    long Version,
    bool HasCurrentQuotaPeriod,
    DateTimeOffset ObservedAt);
