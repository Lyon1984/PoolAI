using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.Modules.Usage.Application;

internal sealed class QuotaReconciliationService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IGroupQuotaReconciliationFactReader factReader,
    IUsageReconciliationProjectionReader projectionReader)
    : IGetGroupQuotaReconciliationUseCase
{
    private readonly IUnitOfWorkFactory _unitOfWorkFactory = unitOfWorkFactory
        ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IGroupQuotaReconciliationFactReader _factReader = factReader
        ?? throw new ArgumentNullException(nameof(factReader));
    private readonly IUsageReconciliationProjectionReader _projectionReader = projectionReader
        ?? throw new ArgumentNullException(nameof(projectionReader));

    public async ValueTask<Result<QuotaReconciliationView>> ExecuteAsync(
        EntityId groupId,
        EntityId? periodId,
        CancellationToken cancellationToken)
    {
        EntityId? resolvedPeriodId = await ResolvePeriodAsync(
            groupId,
            periodId,
            cancellationToken).ConfigureAwait(false);
        if (resolvedPeriodId is null)
        {
            return NotFound();
        }

        UsageReconciliationProjectionSnapshot projection = await ReadProjectionAsync(
            groupId,
            resolvedPeriodId.Value,
            cancellationToken).ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? authoritative = await ReadFactAsync(
            groupId,
            resolvedPeriodId.Value,
            projection.CheckpointSourceEventSequence,
            cancellationToken).ConfigureAwait(false);
        return authoritative is null
            ? NotFound()
            : Result.Success(QuotaReconciliationCalculator.Calculate(
                authoritative,
                projection));
    }

    private async ValueTask<EntityId?> ResolvePeriodAsync(
        EntityId groupId,
        EntityId? periodId,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            EntityId? resolved = await _factReader.ResolvePeriodAsync(
                groupId,
                periodId,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return resolved;
        }
    }

    private async ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadFactAsync(
        EntityId groupId,
        EntityId? periodId,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            GroupQuotaReconciliationFactSnapshot? snapshot = await _factReader
                .ReadAsync(
                    groupId,
                    periodId,
                    checkpointSourceEventSequence,
                    unitOfWork.Context,
                    cancellationToken)
                .ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
    }

    private async ValueTask<UsageReconciliationProjectionSnapshot> ReadProjectionAsync(
        EntityId groupId,
        EntityId periodId,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            UsageReconciliationProjectionSnapshot snapshot = await _projectionReader
                .ReadAsync(
                    groupId,
                    periodId,
                    unitOfWork.Context,
                    cancellationToken)
                .ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
    }

    private static Result<QuotaReconciliationView> NotFound() => Result.Failure<
        QuotaReconciliationView>(
            "resource_not_found",
            "The requested Group quota period was not found.");
}
