namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountRoute(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt);
