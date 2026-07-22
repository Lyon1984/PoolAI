using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed partial class PostgresGroupRepository
{
    public async ValueTask<GroupSlice> ListAsync(
        GroupCursor? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 100);
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = cursor is null ? ListFirstSql : ListAfterSql;
        if (cursor is null)
        {
            command.Parameters.AddWithValue(limit + 1);
        }
        else
        {
            command.Parameters.AddWithValue(cursor.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue(cursor.Id.Value);
            command.Parameters.AddWithValue(limit + 1);
        }

        List<GroupResource> groups = new(limit + 1);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groups.Add(ReadGroup(reader));
        }

        bool hasMore = groups.Count > limit;
        if (hasMore)
        {
            groups.RemoveAt(groups.Count - 1);
        }

        return new GroupSlice(groups, hasMore);
    }

    public async ValueTask<GroupResource?> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

}
