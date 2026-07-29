#pragma warning disable MA0048 // Transport-neutral Group Supply contracts are intentionally collocated.
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application;

public sealed record GroupSupplyBindingView(
    EntityId AccountId,
    bool Enabled,
    int? PriorityOverride,
    int? WeightOverride);

public sealed record GroupSupplyConfigurationView(
    EntityId GroupId,
    EntityId? ChannelId,
    IReadOnlyList<GroupSupplyBindingView> AccountBindings,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GetGroupSupplyConfigurationQuery(
    AccountActor Actor,
    EntityId GroupId);

public sealed record CreateGroupSupplyConfigurationCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    EntityId? ChannelId,
    IReadOnlyList<GroupSupplyBindingView> AccountBindings,
    string? IpAddress,
    string? UserAgent);

public sealed record PatchGroupSupplyConfigurationCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    long ExpectedVersion,
    bool ChannelSpecified,
    EntityId? ChannelId,
    bool AccountBindingsSpecified,
    IReadOnlyList<GroupSupplyBindingView>? AccountBindings,
    string Reason,
    string? IpAddress,
    string? UserAgent);
#pragma warning restore MA0048
