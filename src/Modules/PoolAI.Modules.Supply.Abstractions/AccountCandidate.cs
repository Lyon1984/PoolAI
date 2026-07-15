namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountCandidate(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    UpstreamProvider Provider,
    AccountHealth Health,
    int ConcurrencyLimit,
    long ConfigurationVersion);
