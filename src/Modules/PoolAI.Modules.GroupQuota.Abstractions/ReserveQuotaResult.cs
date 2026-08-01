namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReserveQuotaResult(
    ReservationStatus Status,
    ReservationHandle Reservation,
    QuotaLedgerPosition Quota);
