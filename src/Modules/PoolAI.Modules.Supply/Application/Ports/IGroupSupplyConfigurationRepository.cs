#pragma warning disable MA0048 // Configuration writes are aggregate-root operations on one port.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record GroupSupplyConfigurationCreateWrite(
    EntityId GroupId,
    EntityId? ChannelId,
    IReadOnlyList<GroupSupplyBindingValue> AccountBindings);

internal sealed record GroupSupplyConfigurationPatchWrite(
    EntityId GroupId,
    long ExpectedVersion,
    bool ChannelSpecified,
    EntityId? ChannelId,
    bool AccountBindingsSpecified,
    IReadOnlyList<GroupSupplyBindingValue>? AccountBindings,
    string Reason);

internal enum GroupSupplyMutationDisposition
{
    Written,
    ValidationFailed,
    Conflict,
    NotFound,
    VersionConflict,
}

internal sealed record GroupSupplyMutationResult(
    GroupSupplyMutationDisposition Disposition,
    bool WasChanged,
    GroupSupplyConfigurationResource? Value,
    GroupSupplyConfigurationResource? Before,
    long? CurrentVersion = null);

internal interface IGroupSupplyConfigurationRepository
{
    ValueTask<GroupSupplyConfigurationResource?> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken);

    ValueTask<GroupSupplyMutationResult> CreateAsync(
        GroupSupplyConfigurationCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<GroupSupplyMutationResult> PatchAsync(
        GroupSupplyConfigurationPatchWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
