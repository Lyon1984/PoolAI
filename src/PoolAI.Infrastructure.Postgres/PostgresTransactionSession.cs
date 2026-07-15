using Npgsql;
using PoolAI.BuildingBlocks;

namespace PoolAI.Infrastructure.Postgres;

internal sealed class PostgresTransactionSession : IUnitOfWorkContext
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private int _invalidated;

    internal PostgresTransactionSession(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public NpgsqlCommand CreateCommand(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        EnsureActive();
        NpgsqlCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    internal void EnsureActive()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _invalidated) != 0,
            this);
    }

    internal void Invalidate() => Interlocked.Exchange(ref _invalidated, 1);
}
