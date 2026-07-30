#pragma warning disable MA0048 // Channel persistence request/result types stay with the internal port.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Application.Ports;

internal sealed record ChannelCursor(DateTimeOffset CreatedAt, EntityId Id);

internal sealed record ChannelSlice(
    IReadOnlyList<ChannelResource> Items,
    bool HasMore);

internal sealed record ChannelCreateWrite(
    EntityId ChannelId,
    UpstreamProvider Provider,
    string Name,
    ChannelCapabilitiesValue Capabilities,
    IReadOnlyList<ChannelModelMappingValue> ModelMappings);

internal sealed record ChannelUpdateWrite(
    EntityId ChannelId,
    long ExpectedVersion,
    bool NameSpecified,
    string? Name,
    bool StatusSpecified,
    ChannelResourceStatus? Status,
    bool CapabilitiesSpecified,
    ChannelCapabilitiesValue? Capabilities,
    bool ModelMappingsSpecified,
    IReadOnlyList<ChannelModelMappingValue>? ModelMappings,
    string? Reason);

internal sealed record ChannelRetireWrite(
    EntityId ChannelId,
    long ExpectedVersion,
    string Reason);

internal enum ChannelMutationDisposition
{
    Written,
    ValidationFailed,
    Conflict,
    NotFound,
    VersionConflict,
    LifecycleConflict,
    ChannelInUse,
}

internal sealed record ChannelMutationResult(
    ChannelMutationDisposition Disposition,
    bool WasChanged,
    ChannelResource? Value,
    ChannelResource? Before,
    long? CurrentVersion = null);

internal interface IChannelControlPlaneRepository
{
    ValueTask<ChannelSlice> ListAsync(
        ChannelCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<ChannelResource?> GetAsync(
        EntityId channelId,
        CancellationToken cancellationToken);

    ValueTask<ChannelMutationResult> CreateAsync(
        ChannelCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ChannelMutationResult> UpdateAsync(
        ChannelUpdateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ChannelMutationResult> RetireAsync(
        ChannelRetireWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
