namespace PoolAI.Modules.Usage.Application;

internal enum UsageProjectionReconciliationStatus
{
    Blocked,
    NotStarted,
    Mismatched,
    Lagging,
    Reconciled,
}
