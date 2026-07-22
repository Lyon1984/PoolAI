using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Domain;

internal sealed record GroupResource(
    EntityId Id,
    string Name,
    string? Description,
    GroupLifecycle Lifecycle,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasCurrentQuotaPeriod,
    DateTimeOffset ObservedAt)
{
    internal GroupResourceSnapshot ToSnapshot() => new(
        Id,
        Name,
        Description,
        Lifecycle,
        Version,
        CreatedAt,
        UpdatedAt);
}
