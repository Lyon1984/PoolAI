using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Infrastructure.Persistence;

namespace PoolAI.Modules.Usage.Worker;

internal sealed class UsagePeriodProjectionRebuilder(
    IUnitOfWorkFactory unitOfWorkFactory,
    IGroupQuotaReconciliationFactReader reconciliationFactReader,
    IBoundedUsageRebuildFactReader rebuildFactReader,
    IUsageReconciliationProjectionReader projectionReader,
    IBoundedUsageProjectionWriter projectionWriter,
    IUsageAggregationCheckpoint checkpoint)
{
    internal const int MaximumBucketCount = 744;
    private static readonly TimeSpan CheckpointLeaseDuration = TimeSpan.FromMinutes(5);
    private readonly IUnitOfWorkFactory _unitOfWorkFactory = unitOfWorkFactory
        ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
    private readonly IGroupQuotaReconciliationFactReader _reconciliationFactReader =
        reconciliationFactReader
        ?? throw new ArgumentNullException(nameof(reconciliationFactReader));
    private readonly IBoundedUsageRebuildFactReader _rebuildFactReader = rebuildFactReader
        ?? throw new ArgumentNullException(nameof(rebuildFactReader));
    private readonly IUsageReconciliationProjectionReader _projectionReader = projectionReader
        ?? throw new ArgumentNullException(nameof(projectionReader));
    private readonly IBoundedUsageProjectionWriter _projectionWriter = projectionWriter
        ?? throw new ArgumentNullException(nameof(projectionWriter));
    private readonly IUsageAggregationCheckpoint _checkpoint = checkpoint
        ?? throw new ArgumentNullException(nameof(checkpoint));

    internal async ValueTask<BoundedUsagePeriodRebuildResult> RebuildAsync(
        IWorkerSessionLock jobLock,
        BoundedUsagePeriodRebuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        Validate(request);
        if (jobLock.Job != WorkerJobs.UsageRebuild)
        {
            throw new ArgumentException(
                "The bounded Usage rebuild requires the UsageRebuild job lock.",
                nameof(jobLock));
        }

        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return Outcome(BoundedUsagePeriodRebuildDisposition.OwnershipLost);
        }

        UsageReconciliationProjectionSnapshot before = await ReadProjectionAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (before.CheckpointSourceEventSequence <= 0)
        {
            return Outcome(BoundedUsagePeriodRebuildDisposition.InvalidAuthoritativeState);
        }

        UsageAggregationClaimResult claim = await ClaimCheckpointAsync(
            request.GroupId,
            jobLock,
            cancellationToken).ConfigureAwait(false);
        if (claim.Disposition != UsageAggregationClaimDisposition.Acquired
            || claim.Lease is not { } lease)
        {
            return Outcome(BoundedUsagePeriodRebuildDisposition.Busy);
        }

        return await ExecuteClaimedAndReleaseAsync(
            jobLock,
            request,
            before.CheckpointSourceEventSequence,
            lease,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BoundedUsagePeriodRebuildResult>
        ExecuteClaimedAndReleaseAsync(
            IWorkerSessionLock jobLock,
            BoundedUsagePeriodRebuildRequest request,
            long expectedCheckpointSourceEventSequence,
            UsageAggregationLease lease,
            CancellationToken cancellationToken)
    {
        BoundedUsagePeriodRebuildResult result;
        try
        {
            if (lease.LastEventSequence != expectedCheckpointSourceEventSequence)
            {
                result = Outcome(
                    BoundedUsagePeriodRebuildDisposition.InvalidAuthoritativeState,
                    lease.LastEventSequence);
            }
            else
            {
                result = await RebuildClaimedAsync(
                    jobLock,
                    request,
                    lease,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _ = await ReleaseCheckpointAsync(lease).ConfigureAwait(false);
            throw;
        }

        bool released = await ReleaseCheckpointAsync(lease).ConfigureAwait(false);
        return released
            ? result
            : Outcome(
                BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
                lease.LastEventSequence,
                result.RebuiltBucketCount);
    }

    private async ValueTask<BoundedUsagePeriodRebuildResult> RebuildClaimedAsync(
        IWorkerSessionLock jobLock,
        BoundedUsagePeriodRebuildRequest request,
        UsageAggregationLease lease,
        CancellationToken cancellationToken)
    {
        GroupQuotaReconciliationFactSnapshot? authoritative = await ReadFactAsync(
            request,
            lease.LastEventSequence,
            cancellationToken).ConfigureAwait(false);
        if (!IsAuthoritativeHealthy(authoritative, lease.LastEventSequence))
        {
            return Outcome(
                BoundedUsagePeriodRebuildDisposition.InvalidAuthoritativeState,
                lease.LastEventSequence);
        }

        int rebuiltBucketCount = 0;
        foreach (DateTimeOffset bucketStart in Buckets(request))
        {
            if (!await jobLock.VerifyOwnershipAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                return Outcome(
                    BoundedUsagePeriodRebuildDisposition.OwnershipLost,
                    lease.LastEventSequence,
                    rebuiltBucketCount);
            }

            BoundedUsageRebuildHourSnapshot facts = await ReadHourAsync(
                request,
                bucketStart,
                lease.LastEventSequence,
                cancellationToken).ConfigureAwait(false);
            UsageHourProjection? projection = CreateProjection(facts);
            BoundedUsagePeriodRebuildDisposition? stopped =
                await WriteProjectionFencedAsync(
                    jobLock,
                    lease,
                    request,
                    bucketStart,
                    projection,
                    cancellationToken).ConfigureAwait(false);
            if (stopped is { } disposition)
            {
                return Outcome(
                    disposition,
                    lease.LastEventSequence,
                    rebuiltBucketCount);
            }

            rebuiltBucketCount++;
        }

        return await VerifyWithFinalFenceAsync(
            jobLock,
            request,
            lease,
            rebuiltBucketCount,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<BoundedUsagePeriodRebuildResult>
        VerifyWithFinalFenceAsync(
            IWorkerSessionLock jobLock,
            BoundedUsagePeriodRebuildRequest request,
            UsageAggregationLease lease,
            int rebuiltBucketCount,
            CancellationToken cancellationToken)
    {
        BoundedUsagePeriodRebuildResult verification = await VerifyAsync(
            request,
            lease.LastEventSequence,
            rebuiltBucketCount,
            cancellationToken).ConfigureAwait(false);
        BoundedUsagePeriodRebuildDisposition? finalFence = await ConfirmFencesAsync(
            jobLock,
            lease,
            cancellationToken).ConfigureAwait(false);
        return finalFence is { } finalDisposition
            ? Outcome(finalDisposition, lease.LastEventSequence, rebuiltBucketCount)
            : verification;
    }

    private async ValueTask<BoundedUsagePeriodRebuildDisposition?>
        WriteProjectionFencedAsync(
            IWorkerSessionLock jobLock,
            UsageAggregationLease checkpointLease,
            BoundedUsagePeriodRebuildRequest request,
            DateTimeOffset bucketStart,
            UsageHourProjection? projection,
            CancellationToken cancellationToken)
    {
        IUnitOfWork? unitOfWork = await jobLock
            .TryBeginFencedUnitOfWorkAsync(cancellationToken)
            .ConfigureAwait(false);
        if (unitOfWork is null)
        {
            return BoundedUsagePeriodRebuildDisposition.OwnershipLost;
        }

        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        if (!await _checkpoint.HeartbeatAsync(
                checkpointLease,
                CheckpointLeaseDuration,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false))
        {
            return BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost;
        }

        await _projectionWriter.ReplaceOrDeleteAsync(
            request.GroupId,
            request.PeriodId,
            bucketStart,
            projection,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async ValueTask<BoundedUsagePeriodRebuildResult> VerifyAsync(
        BoundedUsagePeriodRebuildRequest request,
        long checkpointSourceEventSequence,
        int rebuiltBucketCount,
        CancellationToken cancellationToken)
    {
        UsageReconciliationProjectionSnapshot projection = await ReadProjectionAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? authoritative = await ReadFactAsync(
            request,
            checkpointSourceEventSequence,
            cancellationToken).ConfigureAwait(false);
        if (projection.CheckpointSourceEventSequence != checkpointSourceEventSequence
            || !IsAuthoritativeHealthy(authoritative, checkpointSourceEventSequence))
        {
            return Outcome(
                BoundedUsagePeriodRebuildDisposition.InvalidAuthoritativeState,
                checkpointSourceEventSequence,
                rebuiltBucketCount);
        }

        QuotaReconciliationView view = QuotaReconciliationCalculator.Calculate(
            authoritative!,
            projection);
        return new BoundedUsagePeriodRebuildResult(
            view.UsageProjection.ConsumedVariance.IsZero
                ? BoundedUsagePeriodRebuildDisposition.Completed
                : BoundedUsagePeriodRebuildDisposition.StillMismatched,
            checkpointSourceEventSequence,
            rebuiltBucketCount,
            view.UsageProjection.ConsumedVariance);
    }

    private async ValueTask<BoundedUsagePeriodRebuildDisposition?> ConfirmFencesAsync(
        IWorkerSessionLock jobLock,
        UsageAggregationLease lease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork? unitOfWork = await jobLock
            .TryBeginFencedUnitOfWorkAsync(cancellationToken)
            .ConfigureAwait(false);
        if (unitOfWork is null)
        {
            return BoundedUsagePeriodRebuildDisposition.OwnershipLost;
        }

        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        bool heartbeat = await _checkpoint.HeartbeatAsync(
            lease,
            CheckpointLeaseDuration,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (heartbeat)
        {
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return heartbeat
            ? null
            : BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost;
    }

    private async ValueTask<UsageReconciliationProjectionSnapshot> ReadProjectionAsync(
        BoundedUsagePeriodRebuildRequest request,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        UsageReconciliationProjectionSnapshot snapshot = await _projectionReader.ReadAsync(
            request.GroupId,
            request.PeriodId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadFactAsync(
        BoundedUsagePeriodRebuildRequest request,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        GroupQuotaReconciliationFactSnapshot? snapshot =
            await _reconciliationFactReader.ReadAsync(
                request.GroupId,
                request.PeriodId,
                checkpointSourceEventSequence,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<BoundedUsageRebuildHourSnapshot> ReadHourAsync(
        BoundedUsagePeriodRebuildRequest request,
        DateTimeOffset bucketStart,
        long checkpointSourceEventSequence,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        BoundedUsageRebuildHourSnapshot snapshot = await _rebuildFactReader.ReadHourAsync(
            request.GroupId,
            request.PeriodId,
            bucketStart,
            checkpointSourceEventSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<UsageAggregationClaimResult> ClaimCheckpointAsync(
        EntityId groupId,
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        UsageAggregationClaimResult result = await _checkpoint.ClaimAsync(
            new UsageAggregationClaimRequest(
                GroupQuotaUsageProjectorConsumer.ProjectorName,
                PostgresUsageReconciliationProjectionReader.Partition(groupId),
                Owner(jobLock),
                CheckpointLeaseDuration),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<bool> ReleaseCheckpointAsync(
        UsageAggregationLease checkpointLease)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(CancellationToken.None).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        bool released = await _checkpoint.ReleaseAsync(
            checkpointLease,
            unitOfWork.Context,
            CancellationToken.None).ConfigureAwait(false);
        await unitOfWork.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return released;
    }

    private static UsageHourProjection? CreateProjection(
        BoundedUsageRebuildHourSnapshot facts)
    {
        if (facts.Facts.Count == 0)
        {
            return null;
        }

        AttemptSettlementHourSnapshot snapshot = new(
            facts.GroupId,
            facts.PeriodId,
            facts.BucketStart,
            facts.Facts);
        return UsageHourlyProjectionCalculator.TryCreate(snapshot)
            ?? throw new InvalidOperationException(
                "The bounded Usage rebuild facts could not produce a safe projection.");
    }

    private static bool IsAuthoritativeHealthy(
        GroupQuotaReconciliationFactSnapshot? fact,
        long checkpointSourceEventSequence) => fact is not null
        && fact.CheckpointSourceEventSequence == checkpointSourceEventSequence
        && fact.CheckpointBelongsToGroup
        && checkpointSourceEventSequence <= fact.LatestGroupEventSequence
        && fact.LedgerConsumedTokens == fact.FactConsumedTokens
        && fact.LedgerReservedTokens == fact.PendingReservationTokens
        && fact.EventChainConsistent
        && fact.FactEventCoverageConsistent
        && fact.LatestEventMatchesLedger;

    private static IEnumerable<DateTimeOffset> Buckets(
        BoundedUsagePeriodRebuildRequest request)
    {
        for (DateTimeOffset bucket = request.FirstBucketStart; ; bucket = bucket.AddHours(1))
        {
            yield return bucket;
            if (bucket == request.LastBucketStart)
            {
                yield break;
            }
        }
    }

    private static string Owner(IWorkerSessionLock jobLock) => string.Create(
        CultureInfo.InvariantCulture,
        $"quota-rebuild:{jobLock.LockId:x16}");

    private static BoundedUsagePeriodRebuildResult Outcome(
        BoundedUsagePeriodRebuildDisposition disposition,
        long checkpointSourceEventSequence = 0,
        int rebuiltBucketCount = 0) => new(
            disposition,
            checkpointSourceEventSequence,
            rebuiltBucketCount,
            BigInteger.Zero);

    private static void Validate(BoundedUsagePeriodRebuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.GroupId.Value == Guid.Empty || request.PeriodId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The bounded Usage rebuild identifiers must be non-empty.",
                nameof(request));
        }

        if (!IsExactUtcHour(request.FirstBucketStart)
            || !IsExactUtcHour(request.LastBucketStart)
            || request.LastBucketStart < request.FirstBucketStart)
        {
            throw new ArgumentException(
                "The bounded Usage rebuild range must contain ordered exact UTC hours.",
                nameof(request));
        }

        double hours = (request.LastBucketStart - request.FirstBucketStart).TotalHours + 1;
        if (hours > MaximumBucketCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "The bounded Usage rebuild range exceeds its maximum bucket count.");
        }
    }

    private static bool IsExactUtcHour(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
        && value.Minute == 0
        && value.Second == 0
        && value.Ticks % TimeSpan.TicksPerSecond == 0;
}
