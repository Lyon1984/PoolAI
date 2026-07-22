namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public sealed record SubscriptionAccessSnapshot(
    EntityId SubscriptionId,
    EntityId UserId,
    EntityId GroupId,
    string PlanName,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    SubscriptionEffectiveStatus EffectiveStatus,
    long Version,
    DateTimeOffset ObservedAt);
