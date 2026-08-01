namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record QuotaTransitionResult(
    EntityId ReservationId,
    EntityId AttemptId,
    EntityId PeriodId,
    ReservationStatus Status,
    QuotaLedgerPosition Quota);
