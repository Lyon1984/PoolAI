namespace PoolAI.Modules.Operations.Worker;

internal enum OutboxPublishProcessResult
{
    Processed,
    NoWork,
    OwnershipLost,
}
