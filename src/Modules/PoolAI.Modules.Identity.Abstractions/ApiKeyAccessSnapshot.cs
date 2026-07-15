namespace PoolAI.Modules.Identity.Abstractions;

public sealed record ApiKeyAccessSnapshot(
    EntityId ApiKeyId,
    EntityId UserId,
    EntityId GroupId,
    bool IsEffective,
    long Version,
    DateTimeOffset ObservedAt);
