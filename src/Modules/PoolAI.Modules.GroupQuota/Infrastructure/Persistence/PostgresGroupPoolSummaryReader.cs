using System.Numerics;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresGroupPoolSummaryReader(NpgsqlDataSource dataSource) :
    IGroupPoolSummaryReader
{
    private const string SelectSql = """
        SELECT
            groups.id,
            groups.name,
            groups.status,
            period.total_tokens,
            period.consumed_tokens,
            period.reserved_tokens,
            CASE
                WHEN groups.status <> 'active' OR quota.enabled = false THEN 'disabled'
                WHEN period.consumed_tokens >= period.total_tokens THEN 'exhausted'
                ELSE 'active'
            END AS quota_status,
            GREATEST(groups.updated_at, quota.updated_at, period.updated_at) AS updated_at
        FROM public.groups AS groups
        JOIN public.group_token_quotas AS quota
          ON quota.group_id = groups.id
        JOIN public.group_quota_periods AS period
          ON period.id = quota.current_period_id
         AND period.group_id = quota.group_id
         AND period.status = 'current'
        WHERE groups.id = ANY($1)
        ORDER BY array_position($1, groups.id);
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<IReadOnlyList<GroupPoolSummarySnapshot>>> GetByGroupIdsAsync(
        IReadOnlyCollection<EntityId> groupIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        Guid[] ids = groupIds
            .Select(static id => id.Value)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return Result.Success<IReadOnlyList<GroupPoolSummarySnapshot>>(
                Array.Empty<GroupPoolSummarySnapshot>());
        }

        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease = connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = SelectSql;
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = ids,
        });

        List<GroupPoolSummarySnapshot> snapshots = new(ids.Length);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new GroupPoolSummarySnapshot(
                new EntityId(reader.GetGuid(0)),
                reader.GetString(1),
                ParseLifecycle(reader.GetString(2)),
                reader.GetFieldValue<BigInteger>(3),
                reader.GetFieldValue<BigInteger>(4),
                reader.GetFieldValue<BigInteger>(5),
                ParseQuotaStatus(reader.GetString(6)),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return Result.Success<IReadOnlyList<GroupPoolSummarySnapshot>>(snapshots);
    }

    private static GroupLifecycle ParseLifecycle(string value) => value switch
    {
        "active" => GroupLifecycle.Active,
        "disabled" => GroupLifecycle.Disabled,
        "archived" => GroupLifecycle.Archived,
        _ => throw new InvalidOperationException("The persisted Group lifecycle is invalid."),
    };

    private static GroupPoolQuotaStatus ParseQuotaStatus(string value) => value switch
    {
        "active" => GroupPoolQuotaStatus.Active,
        "exhausted" => GroupPoolQuotaStatus.Exhausted,
        "disabled" => GroupPoolQuotaStatus.Disabled,
        _ => throw new InvalidOperationException("The derived Group quota status is invalid."),
    };
}
