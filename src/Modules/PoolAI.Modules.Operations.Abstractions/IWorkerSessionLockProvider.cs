namespace PoolAI.Modules.Operations.Abstractions;

public interface IWorkerSessionLockProvider
{
    ValueTask<IWorkerSessionLock?> TryAcquireAsync(
        WorkerJobIdentity job,
        CancellationToken cancellationToken);
}
