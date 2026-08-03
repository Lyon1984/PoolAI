using System.Numerics;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Usage.Application;

internal static class QuotaReconciliationCalculator
{
    internal static QuotaReconciliationView Calculate(
        GroupQuotaReconciliationFactSnapshot authoritative,
        UsageReconciliationProjectionSnapshot projection)
    {
        ArgumentNullException.ThrowIfNull(authoritative);
        ArgumentNullException.ThrowIfNull(projection);
        if (authoritative.GroupId != projection.GroupId
            || authoritative.PeriodId != projection.PeriodId
            || authoritative.CheckpointSourceEventSequence
                != projection.CheckpointSourceEventSequence)
        {
            throw new InvalidOperationException(
                "The quota reconciliation snapshots do not share one identity and checkpoint.");
        }

        BigInteger consumedVariance = authoritative.LedgerConsumedTokens
            - authoritative.FactConsumedTokens;
        BigInteger reservedVariance = authoritative.LedgerReservedTokens
            - authoritative.PendingReservationTokens;
        BigInteger projectionVariance = authoritative.ExpectedConsumedAtCheckpoint
            - projection.ProjectedConsumedTokens;
        bool authoritativeIntegrity = consumedVariance.IsZero
            && reservedVariance.IsZero
            && authoritative.EventChainConsistent
            && authoritative.FactEventCoverageConsistent
            && authoritative.LatestEventMatchesLedger;

        UsageProjectionReconciliationStatus status = Classify(
            authoritative,
            projection,
            projectionVariance,
            authoritativeIntegrity);
        return new QuotaReconciliationView(
            authoritative,
            consumedVariance,
            reservedVariance,
            new UsageProjectionReconciliation(
                status,
                authoritative.ExpectedConsumedAtCheckpoint,
                projection.ProjectedConsumedTokens,
                projectionVariance,
                projection.CheckpointSourceEventSequence,
                authoritative.LatestPeriodEventSequence,
                projection.DataThrough));
    }

    private static UsageProjectionReconciliationStatus Classify(
        GroupQuotaReconciliationFactSnapshot authoritative,
        UsageReconciliationProjectionSnapshot projection,
        BigInteger projectionVariance,
        bool authoritativeIntegrity)
    {
        if (!authoritativeIntegrity
            || !authoritative.CheckpointBelongsToGroup
            || projection.CheckpointSourceEventSequence
                > authoritative.LatestGroupEventSequence
            || projection.CheckpointSourceEventSequence == 0
                && projection.ProjectedConsumedTokens != BigInteger.Zero)
        {
            return UsageProjectionReconciliationStatus.Blocked;
        }

        if (projection.CheckpointSourceEventSequence == 0)
        {
            return UsageProjectionReconciliationStatus.NotStarted;
        }

        if (!projectionVariance.IsZero)
        {
            return UsageProjectionReconciliationStatus.Mismatched;
        }

        return projection.CheckpointSourceEventSequence
                < authoritative.LatestPeriodEventSequence
            ? UsageProjectionReconciliationStatus.Lagging
            : UsageProjectionReconciliationStatus.Reconciled;
    }
}
