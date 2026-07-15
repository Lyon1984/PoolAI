namespace PoolAI.Modules.Operations.Abstractions;

public interface IWorkerSessionLock : IAsyncDisposable
{
    WorkerJobIdentity Job { get; }

    long LockId { get; }

    ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken);
}
