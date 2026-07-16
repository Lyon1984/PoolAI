namespace PoolAI.Modules.Identity.Worker;

internal enum EmailOutboxProcessResult
{
    NoWork,
    Processed,
    OwnershipLost,
}
