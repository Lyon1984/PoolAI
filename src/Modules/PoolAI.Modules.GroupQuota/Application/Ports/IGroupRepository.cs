#pragma warning disable MA0048 // Repository request/result types are intentionally collocated with the port.
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Application.Ports;

internal sealed record GroupCursor(DateTimeOffset CreatedAt, EntityId Id);

internal sealed record GroupSlice(
    IReadOnlyList<GroupResource> Groups,
    bool HasMore);

internal sealed record CreateGroupWrite(
    EntityId GroupId,
    EntityId PeriodId,
    EntityId QuotaEventId,
    EntityId QuotaOutboxId,
    string Name,
    string? Description,
    long TotalTokens,
    EntityId ActorUserId,
    string QuotaIdempotencyKey,
    string Reason);

internal sealed record UpdateGroupWrite(
    EntityId GroupId,
    long ExpectedVersion,
    bool HasName,
    string? Name,
    bool HasDescription,
    string? Description,
    GroupLifecycle? Lifecycle,
    string? Reason,
    SupplyReadinessEvidence? SupplyEvidence);

internal enum GroupWriteDisposition
{
    Written,
    NameConflict,
    NotFound,
    VersionConflict,
    LifecycleConflict,
    ArchiveBlocked,
    ActivationNotReady,
    ValidationFailed,
}

internal sealed record GroupWriteResult(
    GroupWriteDisposition Disposition,
    GroupResource? Group,
    GroupResource? Before = null,
    bool WasChanged = false,
    long? CurrentVersion = null);

internal interface IGroupRepository
{
    ValueTask<GroupSlice> ListAsync(
        GroupCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<GroupResource?> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken);

    ValueTask<GroupResource?> GetForActivationAsync(
        EntityId groupId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<GroupWriteResult> CreateAsync(
        CreateGroupWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<GroupWriteResult> UpdateAsync(
        UpdateGroupWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
