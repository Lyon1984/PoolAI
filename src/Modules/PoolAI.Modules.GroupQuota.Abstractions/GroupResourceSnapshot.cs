namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupResourceSnapshot(
    EntityId GroupId,
    string Name,
    string? Description,
    GroupLifecycle Lifecycle,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
