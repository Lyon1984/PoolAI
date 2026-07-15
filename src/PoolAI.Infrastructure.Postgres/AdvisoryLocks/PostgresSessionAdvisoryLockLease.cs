using Npgsql;

namespace PoolAI.Infrastructure.Postgres;

internal sealed class PostgresSessionAdvisoryLockLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private NpgsqlConnection? _connection;

    internal PostgresSessionAdvisoryLockLease(long lockId, NpgsqlConnection connection)
    {
        LockId = lockId;
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        BackendProcessId = connection.ProcessID;
    }

    internal long LockId { get; }

    internal int BackendProcessId { get; }

    internal async ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NpgsqlConnection? current = _connection;
            if (current is null || current.FullState != System.Data.ConnectionState.Open)
            {
                return false;
            }

            try
            {
                using NpgsqlCommand command = new("SELECT 1;", current);
                object? scalar = await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                return scalar is 1;
            }
            catch (NpgsqlException)
            {
                return false;
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
            NpgsqlConnection? current = _connection;
            _connection = null;
            if (current is null)
            {
                return;
            }

            try
            {
                if (current.FullState == System.Data.ConnectionState.Open)
                {
                    using NpgsqlCommand command = new(
                        "SELECT pg_catalog.pg_advisory_unlock($1);",
                        current);
                    command.Parameters.AddWithValue(LockId);
                    _ = await command
                        .ExecuteScalarAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (NpgsqlException)
            {
                // A broken PostgreSQL session already released its session advisory lock.
            }
            finally
            {
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
