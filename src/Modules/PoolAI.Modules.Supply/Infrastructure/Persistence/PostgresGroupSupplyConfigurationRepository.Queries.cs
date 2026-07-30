using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed partial class PostgresGroupSupplyConfigurationRepository
{
    private const string SelectColumns = """
        configuration.group_id,
        configuration.channel_id,
        configuration.version,
        configuration.created_at,
        configuration.updated_at,
        binding.account_id,
        binding.is_enabled,
        binding.priority_override,
        binding.weight_override
        """;

    private static readonly string GetSql = $"""
        SELECT {SelectColumns}
        FROM public.group_supply_configurations AS configuration
        LEFT JOIN public.group_accounts AS binding
          ON binding.group_id = configuration.group_id
        WHERE configuration.group_id = $1
        ORDER BY binding.account_id;
        """;

    public async ValueTask<GroupSupplyConfigurationResource?> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = GetSql;
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GroupSupplyConfigurationResource> GetRequiredAsync(
        EntityId groupId,
        PostgresTransactionSession session,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = session.CreateCommand(GetSql);
        command.Parameters.AddWithValue(groupId.Value);
        return await ReadSingleAsync(command, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The written Group Supply Configuration could not be reloaded.");
    }

    private static async ValueTask<GroupSupplyConfigurationResource?> ReadSingleAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        EntityId groupId = new(reader.GetGuid(0));
        EntityId? channelId = reader.IsDBNull(1)
            ? null
            : new EntityId(reader.GetGuid(1));
        long version = reader.GetInt64(2);
        DateTimeOffset createdAt = reader.GetFieldValue<DateTimeOffset>(3);
        DateTimeOffset updatedAt = reader.GetFieldValue<DateTimeOffset>(4);
        List<GroupSupplyBindingValue> bindings = [];
        do
        {
            if (reader.GetGuid(0) != groupId.Value
                || reader.GetInt64(2) != version)
            {
                throw new InvalidOperationException(
                    "The Group Supply Configuration query crossed aggregate roots.");
            }

            if (!reader.IsDBNull(5))
            {
                bindings.Add(new GroupSupplyBindingValue(
                    new EntityId(reader.GetGuid(5)),
                    reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8)));
            }
        }
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        return new GroupSupplyConfigurationResource(
            groupId,
            channelId,
            GroupSupplyInput.Bindings(bindings),
            version,
            createdAt,
            updatedAt);
    }
}
