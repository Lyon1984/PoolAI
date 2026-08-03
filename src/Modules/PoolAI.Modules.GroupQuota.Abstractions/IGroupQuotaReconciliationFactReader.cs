namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Reads bounded, immutable GroupQuota reconciliation facts without exposing
/// GroupQuota persistence to downstream consumers.
/// </summary>
public interface IGroupQuotaReconciliationFactReader
{
    ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
        EntityId groupId,
        EntityId? periodId,
        long checkpointSourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
        ListCurrentCandidatesAsync(
            EntityId? afterGroupId,
            int maximumCount,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<long>> ListPeriodSourceEventSequencesAsync(
        EntityId groupId,
        EntityId periodId,
        long throughSourceEventSequence,
        long afterSourceEventSequence,
        int maximumCount,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
