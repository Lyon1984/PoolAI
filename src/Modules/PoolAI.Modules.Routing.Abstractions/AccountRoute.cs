namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountRoute(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    DateTimeOffset LeaseExpiresAt,
    long SupplyConfigurationVersion,
    long ChannelVersion,
    long AccountVersion);
