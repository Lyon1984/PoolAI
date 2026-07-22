namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public sealed record UserSubscriptionGrantSnapshot(
    EntityId SubscriptionId,
    EntityId UserId,
    EntityId GroupId,
    string PlanName,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt);
