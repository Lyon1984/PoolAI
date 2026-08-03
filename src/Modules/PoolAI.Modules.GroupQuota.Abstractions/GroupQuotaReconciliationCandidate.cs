namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Stable keyset candidate for one Group's current quota period.
/// </summary>
public sealed record GroupQuotaReconciliationCandidate(
    EntityId GroupId,
    EntityId PeriodId);
