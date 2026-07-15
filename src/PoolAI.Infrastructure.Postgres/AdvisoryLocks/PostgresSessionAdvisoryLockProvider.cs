using Npgsql;

namespace PoolAI.Infrastructure.Postgres;

internal sealed class PostgresSessionAdvisoryLockProvider
{
    private readonly NpgsqlDataSource _dataSource;

    internal PostgresSessionAdvisoryLockProvider(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    internal async ValueTask<PostgresSessionAdvisoryLockLease?> TryAcquireAsync(
        long lockId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using NpgsqlCommand command = new(
                "SELECT pg_catalog.pg_try_advisory_lock($1);",
                connection);
            command.Parameters.AddWithValue(lockId);
            object? scalar = await command
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (scalar is not true)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new PostgresSessionAdvisoryLockLease(lockId, connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
