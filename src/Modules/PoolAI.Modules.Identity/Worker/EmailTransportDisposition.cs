namespace PoolAI.Modules.Identity.Worker;

internal enum EmailTransportDisposition
{
    Sent,
    TransientFailure,
    PermanentFailure,
}
