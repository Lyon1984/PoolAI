using Npgsql;
using PoolAI.BuildingBlocks;

namespace PoolAI.Infrastructure.Postgres;

internal sealed class PostgresUnitOfWork : IUnitOfWork
{
    private readonly NpgsqlConnection _connection;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly PostgresTransactionSession _session;
    private readonly NpgsqlTransaction _transaction;
    private UnitOfWorkState _state;

    internal PostgresUnitOfWork(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        _session = new PostgresTransactionSession(connection, transaction);
        Context = _session;
    }

    public IUnitOfWorkContext Context { get; }

    public async ValueTask CommitAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                _state is UnitOfWorkState.Disposed,
                this);
            if (_state is not UnitOfWorkState.Active)
            {
                throw new InvalidOperationException(
                    "A PostgreSQL unit of work can be committed only once.");
            }

            _state = UnitOfWorkState.CommitStarted;
            try
            {
                await _transaction
                    .CommitAsync(cancellationToken)
                    .ConfigureAwait(false);
                _state = UnitOfWorkState.Committed;
            }
            catch
            {
                _state = UnitOfWorkState.CommitFailed;
                throw;
            }
            finally
            {
                _session.Invalidate();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_state is UnitOfWorkState.Disposed)
            {
                return;
            }

            _session.Invalidate();
            try
            {
                if (_state is not UnitOfWorkState.Committed)
                {
                    await _transaction
                        .RollbackAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    await _transaction.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await _connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _state = UnitOfWorkState.Disposed;
            _lifecycleGate.Release();
        }
    }

    private enum UnitOfWorkState
    {
        Active,
        CommitStarted,
        Committed,
        CommitFailed,
        Disposed,
    }
}
