using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Workers;

internal sealed class PostgresWorkerSessionLockProvider(
    PostgresSessionAdvisoryLockProvider advisoryLocks)
    : IWorkerSessionLockProvider
{
    private readonly PostgresSessionAdvisoryLockProvider _advisoryLocks =
        advisoryLocks ?? throw new ArgumentNullException(nameof(advisoryLocks));

    public async ValueTask<IWorkerSessionLock?> TryAcquireAsync(
        WorkerJobIdentity job,
        CancellationToken cancellationToken)
    {
        long lockId = WorkerSessionLockId.Derive(job);
        PostgresSessionAdvisoryLockLease? lease = await _advisoryLocks
            .TryAcquireAsync(lockId, cancellationToken)
            .ConfigureAwait(false);
        return lease is null ? null : new PostgresWorkerSessionLock(job, lease);
    }

    private sealed class PostgresWorkerSessionLock(
        WorkerJobIdentity job,
        PostgresSessionAdvisoryLockLease lease) : IWorkerSessionLock
    {
        private readonly PostgresSessionAdvisoryLockLease _lease = lease;

        public WorkerJobIdentity Job { get; } = job;

        public long LockId => _lease.LockId;

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken) =>
            _lease.VerifyOwnershipAsync(cancellationToken);

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }
}
