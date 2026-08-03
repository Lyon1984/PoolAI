namespace PoolAI.Modules.Usage.Worker;

internal sealed record QuotaReconciliationProcessResult(
    int PageCount,
    int ScannedCount,
    bool OwnershipLost,
    QuotaReconciliationMetricSnapshot Metrics);
