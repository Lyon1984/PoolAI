#pragma warning disable MA0048 // Binding values are children of the Configuration aggregate snapshot.
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Domain;

internal sealed record GroupSupplyBindingValue(
    EntityId AccountId,
    bool Enabled,
    int? PriorityOverride,
    int? WeightOverride);

internal sealed record GroupSupplyConfigurationResource(
    EntityId GroupId,
    EntityId? ChannelId,
    IReadOnlyList<GroupSupplyBindingValue> AccountBindings,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
#pragma warning restore MA0048
