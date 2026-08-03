using System.Numerics;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.Modules.Usage.Infrastructure.Persistence;

internal sealed class PostgresUsageHourlyProjectionWriter :
    IUsageHourlyProjectionWriter,
    IBoundedUsageProjectionWriter
{
    private const string UpsertGroupSql = """
        INSERT INTO public.group_usage_hourly (
            group_id, period_id, bucket_start,
            request_count, attempt_count, failure_count, failover_count,
            estimated_attempt_count, input_tokens, output_tokens,
            cache_creation_tokens, cache_read_tokens, thinking_tokens, total_tokens
        ) VALUES (
            $1, $2, $3, $4, $5, $6, $7,
            $8, $9, $10, $11, $12, $13, $14
        )
        ON CONFLICT (group_id, period_id, bucket_start) DO UPDATE
        SET request_count = EXCLUDED.request_count,
            attempt_count = EXCLUDED.attempt_count,
            failure_count = EXCLUDED.failure_count,
            failover_count = EXCLUDED.failover_count,
            estimated_attempt_count = EXCLUDED.estimated_attempt_count,
            input_tokens = EXCLUDED.input_tokens,
            output_tokens = EXCLUDED.output_tokens,
            cache_creation_tokens = EXCLUDED.cache_creation_tokens,
            cache_read_tokens = EXCLUDED.cache_read_tokens,
            thinking_tokens = EXCLUDED.thinking_tokens,
            total_tokens = EXCLUDED.total_tokens,
            rebuilt_at = clock_timestamp(),
            version = group_usage_hourly.version + 1;
        """;

    private const string DeleteStaleAccountsSql = """
        DELETE FROM public.account_usage_hourly
        WHERE group_id = $1
          AND period_id = $2
          AND bucket_start = $3
          AND NOT (account_id = ANY($4::uuid[]));
        """;

    private const string DeleteHourAccountsSql = """
        DELETE FROM public.account_usage_hourly
        WHERE group_id = $1
          AND period_id = $2
          AND bucket_start = $3;
        """;

    private const string DeleteHourGroupSql = """
        DELETE FROM public.group_usage_hourly
        WHERE group_id = $1
          AND period_id = $2
          AND bucket_start = $3;
        """;

    private const string UpsertAccountSql = """
        INSERT INTO public.account_usage_hourly (
            group_id, account_id, period_id, bucket_start,
            request_count, attempt_count, failure_count, failover_count,
            estimated_attempt_count, input_tokens, output_tokens,
            cache_creation_tokens, cache_read_tokens, thinking_tokens, total_tokens
        ) VALUES (
            $1, $2, $3, $4, $5, $6, $7, $8,
            $9, $10, $11, $12, $13, $14, $15
        )
        ON CONFLICT (group_id, account_id, period_id, bucket_start) DO UPDATE
        SET request_count = EXCLUDED.request_count,
            attempt_count = EXCLUDED.attempt_count,
            failure_count = EXCLUDED.failure_count,
            failover_count = EXCLUDED.failover_count,
            estimated_attempt_count = EXCLUDED.estimated_attempt_count,
            input_tokens = EXCLUDED.input_tokens,
            output_tokens = EXCLUDED.output_tokens,
            cache_creation_tokens = EXCLUDED.cache_creation_tokens,
            cache_read_tokens = EXCLUDED.cache_read_tokens,
            thinking_tokens = EXCLUDED.thinking_tokens,
            total_tokens = EXCLUDED.total_tokens,
            rebuilt_at = clock_timestamp(),
            version = account_usage_hourly.version + 1;
        """;

    public async ValueTask ReplaceAsync(
        UsageHourProjection projection,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using (NpgsqlCommand group = session.CreateCommand(UpsertGroupSql))
        {
            group.Parameters.AddWithValue(projection.GroupId.Value);
            group.Parameters.AddWithValue(projection.PeriodId.Value);
            group.Parameters.AddWithValue(projection.BucketStart);
            AddAggregate(group.Parameters, projection.Group);
            RequireOne(await group.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand delete = session.CreateCommand(DeleteStaleAccountsSql))
        {
            delete.Parameters.AddWithValue(projection.GroupId.Value);
            delete.Parameters.AddWithValue(projection.PeriodId.Value);
            delete.Parameters.AddWithValue(projection.BucketStart);
            delete.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                Value = projection.Accounts
                    .Select(static account => account.AccountId.Value)
                    .ToArray(),
            });
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (AccountUsageHourProjection account in projection.Accounts)
        {
            using NpgsqlCommand command = session.CreateCommand(UpsertAccountSql);
            command.Parameters.AddWithValue(projection.GroupId.Value);
            command.Parameters.AddWithValue(account.AccountId.Value);
            command.Parameters.AddWithValue(projection.PeriodId.Value);
            command.Parameters.AddWithValue(projection.BucketStart);
            AddAggregate(command.Parameters, account.Aggregate);
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false));
        }
    }

    public async ValueTask ReplaceOrDeleteAsync(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        UsageHourProjection? projection,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        if (projection is not null)
        {
            if (projection.GroupId != groupId
                || projection.PeriodId != periodId
                || projection.BucketStart != bucketStart)
            {
                throw new ArgumentException(
                    "The bounded Usage projection does not match its target bucket.",
                    nameof(projection));
            }

            await ReplaceAsync(
                projection,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using (NpgsqlCommand accounts = session.CreateCommand(DeleteHourAccountsSql))
        {
            AddBucketIdentity(accounts, groupId, periodId, bucketStart);
            _ = await accounts.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using NpgsqlCommand group = session.CreateCommand(DeleteHourGroupSql);
        AddBucketIdentity(group, groupId, periodId, bucketStart);
        int affected = await group.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (affected is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage bounded delete violated its ABI.");
        }
    }

    private static void AddAggregate(
        NpgsqlParameterCollection parameters,
        UsageHourlyAggregate aggregate)
    {
        parameters.AddWithValue(aggregate.RequestCount);
        parameters.AddWithValue(aggregate.AttemptCount);
        parameters.AddWithValue(aggregate.FailureCount);
        parameters.AddWithValue(aggregate.FailoverCount);
        parameters.AddWithValue(aggregate.EstimatedAttemptCount);
        AddNumeric(parameters, aggregate.InputTokens);
        AddNumeric(parameters, aggregate.OutputTokens);
        AddNumeric(parameters, aggregate.CacheCreationTokens);
        AddNumeric(parameters, aggregate.CacheReadTokens);
        AddNumeric(parameters, aggregate.ThinkingTokens);
        AddNumeric(parameters, aggregate.TotalTokens);
    }

    private static void AddBucketIdentity(
        NpgsqlCommand command,
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart)
    {
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(bucketStart);
    }

    private static void AddNumeric(
        NpgsqlParameterCollection parameters,
        BigInteger value) => parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value,
        });

    private static void RequireOne(int affected)
    {
        if (affected != 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Usage hourly projection write violated its ABI.");
        }
    }
}
