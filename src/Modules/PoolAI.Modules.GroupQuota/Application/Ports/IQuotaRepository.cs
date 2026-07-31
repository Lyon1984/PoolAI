#pragma warning disable MA0048 // Quota-period repository requests and results are intentionally collocated.
using System.Numerics;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Application.Ports;

internal sealed record AdjustQuotaWrite(
    EntityId GroupId,
    BigInteger NewTotalTokens,
    long ExpectedVersion,
    EntityId ActorUserId,
    EntityId EventId,
    EntityId OutboxId,
    string EventIdempotencyKey,
    string Reason);

internal sealed record ResetQuotaWrite(
    EntityId GroupId,
    EntityId NewPeriodId,
    BigInteger TotalTokens,
    long ExpectedVersion,
    EntityId ActorUserId,
    EntityId EventId,
    EntityId OutboxId,
    string EventIdempotencyKey,
    string Reason);

internal enum QuotaWriteDisposition
{
    Written,
    NotFound,
    Archived,
    VersionConflict,
    IdempotencyConflict,
    Conflict,
}

internal sealed record QuotaWriteResult(
    QuotaWriteDisposition Disposition,
    GroupQuotaResource? Before = null,
    GroupQuotaResource? After = null,
    long? CurrentVersion = null);

internal interface IQuotaRepository
{
    ValueTask<GroupQuotaResource?> GetCurrentAsync(
        EntityId groupId,
        CancellationToken cancellationToken);

    ValueTask<QuotaWriteResult> AdjustTotalAsync(
        AdjustQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<QuotaWriteResult> ResetAsync(
        ResetQuotaWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
