namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupActivationResult(EntityId GroupId, GroupLifecycle Lifecycle, long Version);
