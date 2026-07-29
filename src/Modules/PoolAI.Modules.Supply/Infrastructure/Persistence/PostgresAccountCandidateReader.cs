using System.Runtime.CompilerServices;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresAccountCandidateReader(
    NpgsqlDataSource dataSource) : IAccountCandidateReader
{
    private const string CandidatesSql = """
        SELECT configuration.group_id,
               channel.id,
               account.id,
               channel.provider,
               mapping.client_model,
               mapping.upstream_model,
               account.upstream_base_url,
               (channel.capabilities ->> 'responses')::boolean,
               (channel.capabilities ->> 'chat_completions')::boolean,
               (channel.capabilities ->> 'function_tools')::boolean,
               (channel.capabilities ->> 'streaming')::boolean,
               account.last_health_status,
               account.max_concurrency,
               COALESCE(binding.priority_override, account.priority),
               COALESCE(binding.weight_override, account.weight),
               configuration.version,
               channel.version,
               account.version
        FROM public.group_supply_configurations AS configuration
        JOIN public.channels AS channel
          ON channel.id = configuration.channel_id
        CROSS JOIN LATERAL pg_catalog.jsonb_each_text(channel.model_rules)
          AS mapping(client_model, upstream_model)
        JOIN public.group_accounts AS binding
          ON binding.group_id = configuration.group_id
        JOIN public.accounts AS account
          ON account.id = binding.account_id
        WHERE configuration.group_id = $1
          AND mapping.client_model = $2
          AND channel.status = 'active'
          AND channel.deleted_at IS NULL
          AND binding.is_enabled = true
          AND account.status = 'active'
          AND account.deleted_at IS NULL
          AND account.last_health_status IN ('healthy', 'degraded')
          AND (
              account.upstream_rate_limited_until IS NULL
              OR account.upstream_rate_limited_until <= clock_timestamp()
          )
          AND account.provider = channel.provider
        ORDER BY
            COALESCE(binding.priority_override, account.priority) DESC,
            CASE account.last_health_status
                WHEN 'healthy' THEN 0
                ELSE 1
            END,
            account.id;
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<IReadOnlyList<AccountCandidate>>> GetCandidatesAsync(
        EntityId groupId,
        string model,
        CancellationToken cancellationToken)
    {
        string canonicalModel;
        try
        {
            canonicalModel = ChannelInput.ModelName(model, nameof(model));
        }
        catch (ArgumentException)
        {
            return Result.Failure<IReadOnlyList<AccountCandidate>>(
                "validation_failed",
                "The requested model is invalid.");
        }

        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = CandidatesSql;
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(canonicalModel);
        List<AccountCandidate> candidates = [];
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.Add(ReadCandidate(reader));
        }

        return Result.Success<IReadOnlyList<AccountCandidate>>(candidates);
    }

    private static AccountCandidate ReadCandidate(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(0)),
        new EntityId(reader.GetGuid(1)),
        new EntityId(reader.GetGuid(2)),
        ParseProvider(reader.GetString(3)),
        reader.GetString(4),
        reader.GetString(5),
        AccountInput.BaseUrl(reader.GetString(6)),
        new ChannelCapabilitiesSnapshot(
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetBoolean(10)),
        ParseHealth(reader.GetString(11)),
        reader.GetInt32(12),
        reader.GetInt32(13),
        reader.GetInt32(14),
        reader.GetInt64(15),
        reader.GetInt64(16),
        reader.GetInt64(17));

    private static UpstreamProvider ParseProvider(string value) => value switch
    {
        "openai" => UpstreamProvider.OpenAi,
        "openai_compatible" => UpstreamProvider.OpenAiCompatible,
        _ => throw new InvalidOperationException(
            "The candidate Channel provider is invalid."),
    };

    private static AccountHealth ParseHealth(string value) => value switch
    {
        "healthy" => AccountHealth.Healthy,
        "degraded" => AccountHealth.Degraded,
        _ => throw new InvalidOperationException(
            "The candidate Account health is not schedulable."),
    };
}
