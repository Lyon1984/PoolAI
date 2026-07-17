using PoolAI.Modules.SubscriptionAccess.Application;

namespace PoolAI.Modules.SubscriptionAccess.Domain;

internal sealed record SubscriptionTemplateRecord(
    EntityId Id,
    EntityId GroupId,
    string Name,
    string? Description,
    int DefaultDurationDays,
    SubscriptionTemplateLifecycle Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal SubscriptionTemplateView ToView() => new(
        Id,
        GroupId,
        Name,
        Description,
        DefaultDurationDays,
        Status,
        Version,
        CreatedAt,
        UpdatedAt);
}
