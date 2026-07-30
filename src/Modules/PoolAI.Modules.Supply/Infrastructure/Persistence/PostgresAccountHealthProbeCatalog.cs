using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal sealed class PostgresAccountHealthProbeCatalog(
    NpgsqlDataSource dataSource) : IAccountHealthProbeCatalog
{
    private const string DueBatchSql = """
        SELECT account.id,
               account.last_health_status,
               account.max_concurrency,
               account.upstream_rate_limited_until,
               account.last_health_at,
               account.version,
               account.credential_revision,
               account.status
        FROM public.accounts AS account
        WHERE ($1::uuid IS NULL OR account.id > $1)
          AND account.deleted_at IS NULL
          AND (
              (
                  account.status = 'active'
                  AND account.last_health_status = 'unknown'
              )
              OR (
                  account.status = 'disabled'
                  AND account.last_health_status = 'unknown'
                  AND account.last_health_at IS NULL
              )
              OR (
                  account.status = 'active'
                  AND
                  account.last_health_status IN ('healthy', 'degraded')
                  AND (
                      account.last_health_at IS NULL
                      OR account.last_health_at
                          <= clock_timestamp() - $2::interval
                  )
              )
              OR (
                  account.status = 'active'
                  AND
                  account.last_health_status = 'cooling'
                  AND account.upstream_rate_limited_until IS NOT NULL
                  AND account.upstream_rate_limited_until <= clock_timestamp()
              )
          )
        ORDER BY account.id
        LIMIT $3;
        """;

    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<Result<IReadOnlyList<AccountHealthProbeCandidate>>>
        GetDueBatchAsync(
            EntityId? afterExclusive,
            int maximumCount,
            TimeSpan healthyProbeInterval,
            CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        if (healthyProbeInterval < TimeSpan.FromSeconds(1)
            || healthyProbeInterval > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(healthyProbeInterval));
        }

        NpgsqlConnection connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable connectionLease =
            connection.ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = DueBatchSql;
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = afterExclusive is null
                ? DBNull.Value
                : afterExclusive.Value.Value,
        });
        command.Parameters.AddWithValue(healthyProbeInterval);
        command.Parameters.AddWithValue(maximumCount);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AccountHealthProbeCandidate> candidates = new(maximumCount);
        Guid? previous = afterExclusive?.Value;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AccountHealthProbeCandidate candidate = ReadCandidate(reader);
            if (previous is not null
                && candidate.AccountId.Value.CompareTo(previous.Value) <= 0)
            {
                throw new InvalidOperationException(
                    "The Account health probe catalog returned a non-keyset page.");
            }

            candidates.Add(candidate);
            previous = candidate.AccountId.Value;
        }

        return Result.Success<IReadOnlyList<AccountHealthProbeCandidate>>(
            candidates);
    }

    private static AccountHealthProbeCandidate ReadCandidate(
        NpgsqlDataReader reader)
    {
        int concurrencyLimit = reader.GetInt32(2);
        long accountVersion = reader.GetInt64(5);
        long credentialRevision = reader.GetInt64(6);
        string lifecycle = reader.GetString(7);
        if (concurrencyLimit is <= 0 or > 10_000)
        {
            throw new InvalidOperationException(
                "The Account health probe candidate has an invalid concurrency limit.");
        }

        if (accountVersion <= 0 || credentialRevision <= 0)
        {
            throw new InvalidOperationException(
                "The Account health probe candidate has invalid fencing versions.");
        }

        if (lifecycle is not ("active" or "disabled"))
        {
            throw new InvalidOperationException(
                "The Account health probe candidate has an invalid lifecycle.");
        }

        return new(
            new EntityId(reader.GetGuid(0)),
            ParseHealth(reader.GetString(1)),
            concurrencyLimit,
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            accountVersion,
            credentialRevision,
            string.Equals(lifecycle, "active", StringComparison.Ordinal));
    }

    private static AccountHealth ParseHealth(string value) => value switch
    {
        "unknown" => AccountHealth.Unknown,
        "healthy" => AccountHealth.Healthy,
        "degraded" => AccountHealth.Degraded,
        "cooling" => AccountHealth.Cooling,
        _ => throw new InvalidOperationException(
            "The Account health probe candidate has an invalid health state."),
    };
}
