namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountProbeLeaseAcquireCommand(
    EntityId AccountId,
    int ConcurrencyLimit);
