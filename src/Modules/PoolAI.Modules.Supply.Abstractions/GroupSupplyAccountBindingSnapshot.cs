namespace PoolAI.Modules.Supply.Abstractions;

public sealed record GroupSupplyAccountBindingSnapshot(
    EntityId AccountId,
    bool Enabled,
    int? PriorityOverride,
    int? WeightOverride);
