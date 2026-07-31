using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed partial class PostgresQuotaRepository
{
    public async ValueTask<GroupQuotaResource?> GetCurrentAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectCurrentSql;
        command.Parameters.AddWithValue(groupId.Value);
        return await PostgresQuotaAbiContract
            .ReadSnapshotAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }
}
