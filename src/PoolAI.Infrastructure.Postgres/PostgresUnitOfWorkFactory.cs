using System.Data;
using Npgsql;
using PoolAI.BuildingBlocks;

namespace PoolAI.Infrastructure.Postgres;

internal sealed class PostgresUnitOfWorkFactory : IUnitOfWorkFactory
{
    private readonly NpgsqlDataSource _dataSource;

    internal PostgresUnitOfWorkFactory(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            NpgsqlTransaction transaction = await connection
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            return new PostgresUnitOfWork(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
