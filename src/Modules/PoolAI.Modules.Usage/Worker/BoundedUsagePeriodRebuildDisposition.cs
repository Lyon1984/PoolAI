namespace PoolAI.Modules.Usage.Worker;

internal enum BoundedUsagePeriodRebuildDisposition
{
    Completed,
    Busy,
    OwnershipLost,
    CheckpointLeaseLost,
    InvalidAuthoritativeState,
    StillMismatched,
}
