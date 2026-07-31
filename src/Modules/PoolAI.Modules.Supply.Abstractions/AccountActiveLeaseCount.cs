namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountActiveLeaseCount(
    EntityId AccountId,
    int ActiveLeases);
