namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CoordinationProbeAcquireRequest(
    EntityId AccountId,
    string Owner);
