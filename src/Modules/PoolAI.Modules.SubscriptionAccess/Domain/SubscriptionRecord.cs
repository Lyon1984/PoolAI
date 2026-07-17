using PoolAI.Modules.SubscriptionAccess.Application;

namespace PoolAI.Modules.SubscriptionAccess.Domain;

internal sealed record SubscriptionRecord(
    EntityId Id,
    EntityId UserId,
    EntityId GroupId,
    EntityId TemplateId,
    string PlanName,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    SubscriptionLifecycle Status,
    SubscriptionEffectiveLifecycle EffectiveStatus,
    EntityId AssignedBy,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ObservedAt)
{
    internal SubscriptionView ToView() => new(
        Id,
        UserId,
        GroupId,
        TemplateId,
        PlanName,
        StartsAt,
        ExpiresAt,
        Status,
        EffectiveStatus,
        AssignedBy,
        Version,
        CreatedAt,
        UpdatedAt,
        ObservedAt);
}
