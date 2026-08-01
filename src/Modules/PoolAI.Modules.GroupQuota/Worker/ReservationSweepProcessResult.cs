namespace PoolAI.Modules.GroupQuota.Worker;

internal sealed record ReservationSweepProcessResult(
    ReservationSweepProcessDisposition Disposition,
    int PageCount,
    int ScannedCount,
    int ExpiredCount,
    int RaceLostCount);
