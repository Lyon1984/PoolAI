using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresModelCatalog(
    NpgsqlDataSource dataSource) : IModelCatalog
{
    private const string ModelsSql = """
        SELECT DISTINCT mapping.client_model
        FROM public.group_supply_configurations AS configuration
        JOIN public.channels AS channel
          ON channel.id = configuration.channel_id
        CROSS JOIN LATERAL pg_catalog.jsonb_each_text(channel.model_rules)
          AS mapping(client_model, upstream_model)
        WHERE configuration.group_id = $1
          AND channel.status = 'active'
          AND channel.deleted_at IS NULL
          AND EXISTS (
              SELECT 1
              FROM public.group_accounts AS binding
              JOIN public.accounts AS account
                ON account.id = binding.account_id
              WHERE binding.group_id = configuration.group_id
                AND binding.is_enabled = true
                AND account.status = 'active'
                AND account.deleted_at IS NULL
                AND account.last_health_status IN ('healthy', 'degraded')
                AND (
                    account.upstream_rate_limited_until IS NULL
                    OR account.upstream_rate_limited_until <= clock_timestamp()
                )
                AND account.provider = channel.provider
          );
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<IReadOnlyList<string>>> GetModelsAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = ModelsSql;
        command.Parameters.AddWithValue(groupId.Value);
        List<string> models = [];
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            models.Add(reader.GetString(0));
        }

        models.Sort(StringComparer.Ordinal);
        return Result.Success<IReadOnlyList<string>>(models);
    }
}
