namespace PoolAI.Modules.Supply.Abstractions;

public sealed record GroupSupplyConfigurationSnapshot(
    EntityId GroupId,
    EntityId? ChannelId,
    IReadOnlyList<GroupSupplyAccountBindingSnapshot> AccountBindings,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
