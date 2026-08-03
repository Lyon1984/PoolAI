namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Exact authoritative and checkpoint-pinned facts for one Group quota period.
/// </summary>
public sealed record GroupQuotaReconciliationFactSnapshot(
    EntityId GroupId,
    EntityId PeriodId,
    long CheckpointSourceEventSequence,
    BigInteger LedgerTotalTokens,
    BigInteger LedgerConsumedTokens,
    BigInteger LedgerReservedTokens,
    BigInteger FactConsumedTokens,
    BigInteger PendingReservationTokens,
    long PendingReservationCount,
    long OverdueReservationCount,
    DateTimeOffset? OldestOverdueAt,
    BigInteger ExpectedConsumedAtCheckpoint,
    bool CheckpointBelongsToGroup,
    long LatestPeriodEventSequence,
    DateTimeOffset LatestPeriodEventOccurredAt,
    bool EventChainConsistent,
    bool FactEventCoverageConsistent,
    bool LatestEventMatchesLedger,
    BigInteger OverageTokens,
    DateTimeOffset CheckedAt,
    bool IsCurrentPeriod,
    long FirstPeriodEventSequence,
    long LatestGroupEventSequence,
    long PeriodEventCount);
