namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public sealed record SubscriptionAccessSnapshot(
    EntityId SubscriptionId,
    EntityId UserId,
    EntityId GroupId,
    SubscriptionEffectiveStatus EffectiveStatus,
    long Version,
    DateTimeOffset ObservedAt);
