using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresBoundedUsagePeriodRebuilderTests(
    PostgresRuntimeFixture fixture)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RebuildRestoresCheckpointFactsAndChangesOnlyDerivedBuckets()
    {
        // Governing contract: ADR 0013 bounded projection-only recovery boundary.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RebuildScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        ImmutableState before = await ReadImmutableStateAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await AssertDamagedProjectionAsync(scenario, cancellationToken)
            .ConfigureAwait(true);

        BoundedUsagePeriodRebuildResult result = await RebuildSeededRangeAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);

        AssertCompletedRebuild(scenario, result);
        await AssertRebuiltProjectionAsync(scenario, cancellationToken)
            .ConfigureAwait(true);

        ImmutableState after = await ReadImmutableStateAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(before, after);
    }

    private async ValueTask AssertDamagedProjectionAsync(
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        Assert.Equal(new BigInteger(999), await ReadGroupTotalAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(3, await ReadAccountCountAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(false));
        Assert.True(await GroupBucketExistsAsync(
            scenario,
            scenario.EmptyBucketStart,
            cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<BoundedUsagePeriodRebuildResult> RebuildSeededRangeAsync(
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        IWorkerSessionLock jobLock = await AcquireRebuildLockAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable jobLockLease =
            jobLock.ConfigureAwait(false);
        return await ResolveRebuilder().RebuildAsync(
            jobLock,
            new BoundedUsagePeriodRebuildRequest(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.BucketStart,
                scenario.EmptyBucketStart),
            cancellationToken).ConfigureAwait(false);
    }

    private static void AssertCompletedRebuild(
        RebuildScenario scenario,
        BoundedUsagePeriodRebuildResult result)
    {
        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Completed, result.Disposition);
        Assert.Equal(scenario.CheckpointSequence, result.CheckpointSourceEventSequence);
        Assert.Equal(2, result.RebuiltBucketCount);
        Assert.Equal(BigInteger.Zero, result.RemainingProjectionVariance);
    }

    private async ValueTask AssertRebuiltProjectionAsync(
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        HourlyProjectionState group = Assert.IsType<HourlyProjectionState>(
            await ReadGroupProjectionAsync(
                scenario,
                scenario.BucketStart,
                cancellationToken).ConfigureAwait(false));
        Assert.Equal(ExpectedGroupProjection, group);

        IReadOnlyDictionary<EntityId, HourlyProjectionState> accounts =
            await ReadAccountProjectionsAsync(
                scenario,
                scenario.BucketStart,
                cancellationToken).ConfigureAwait(false);
        Assert.Equal(2, accounts.Count);
        Assert.Equal(ExpectedAdjustedAccountProjection, accounts[scenario.AdjustedAccountId]);
        Assert.Equal(ExpectedBaseAccountProjection, accounts[scenario.BaseAccountId]);
        Assert.DoesNotContain(scenario.StaleAccountId, accounts.Keys);
        Assert.False(await GroupBucketExistsAsync(
            scenario,
            scenario.EmptyBucketStart,
            cancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await ReadAccountCountAsync(
            scenario,
            scenario.EmptyBucketStart,
            cancellationToken).ConfigureAwait(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpiredOrStolenCheckpointAfterFactReadCannotCommitAStaleBucket(
        bool replaceOwner)
    {
        // Governing contract: ADR 0013 requires checkpoint fencing in the bucket UoW.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RebuildScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        IBoundedUsageRebuildFactReader inner = fixture.WorkerServices
            .GetRequiredService<IBoundedUsageRebuildFactReader>();
        SabotagingRebuildFactReader reader = new(
            inner,
            token => InvalidateCheckpointAsync(scenario, replaceOwner, token));
        UsagePeriodProjectionRebuilder rebuilder = ResolveRebuilder(reader);
        IWorkerSessionLock jobLock = await AcquireRebuildLockAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable jobLockLease =
            jobLock.ConfigureAwait(true);

        BoundedUsagePeriodRebuildResult result = await rebuilder.RebuildAsync(
            jobLock,
            new BoundedUsagePeriodRebuildRequest(
                scenario.GroupId,
                scenario.PeriodId,
                scenario.BucketStart,
                scenario.BucketStart),
            cancellationToken);

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
            result.Disposition);
        Assert.Equal(0, result.RebuiltBucketCount);
        Assert.Equal(new BigInteger(999), await ReadGroupTotalAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(3, await ReadAccountCountAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TerminatedJobLockSessionInsideBucketUnitRollsBackProjectionWrite()
    {
        // Governing contract: the bucket UoW must stay on the advisory-lock session.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        RebuildScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        IWorkerSessionLock jobLock = await AcquireRebuildLockAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable jobLockLease =
            jobLock.ConfigureAwait(true);
        IBoundedUsageProjectionWriter innerWriter = fixture.WorkerServices
            .GetRequiredService<IBoundedUsageProjectionWriter>();
        SabotagingProjectionWriter writer = new(
            innerWriter,
            token => TerminateJobLockSessionAsync(jobLock, token));
        UsagePeriodProjectionRebuilder rebuilder = ResolveRebuilder(
            projectionWriter: writer);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            rebuilder.RebuildAsync(
                jobLock,
                new BoundedUsagePeriodRebuildRequest(
                    scenario.GroupId,
                    scenario.PeriodId,
                    scenario.BucketStart,
                    scenario.BucketStart),
                cancellationToken).AsTask());

        Assert.True(failure is ObjectDisposedException or NpgsqlException);
        Assert.Equal(new BigInteger(999), await ReadGroupTotalAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(3, await ReadAccountCountAsync(
            scenario,
            scenario.BucketStart,
            cancellationToken).ConfigureAwait(true));
    }

    private UsagePeriodProjectionRebuilder ResolveRebuilder(
        IBoundedUsageRebuildFactReader? rebuildFactReader = null,
        IBoundedUsageProjectionWriter? projectionWriter = null) => new(
        fixture.WorkerServices.GetRequiredService<IUnitOfWorkFactory>(),
        fixture.WorkerServices.GetRequiredService<
            IGroupQuotaReconciliationFactReader>(),
        rebuildFactReader ?? fixture.WorkerServices
            .GetRequiredService<IBoundedUsageRebuildFactReader>(),
        fixture.WorkerServices.GetRequiredService<IUsageReconciliationProjectionReader>(),
        projectionWriter ?? fixture.WorkerServices
            .GetRequiredService<IBoundedUsageProjectionWriter>(),
        fixture.WorkerServices.GetRequiredService<IUsageAggregationCheckpoint>());

    private async ValueTask<IWorkerSessionLock> AcquireRebuildLockAsync(
        CancellationToken cancellationToken)
    {
        IWorkerSessionLockProvider provider = fixture.WorkerServices
            .GetRequiredService<IWorkerSessionLockProvider>();
        return Assert.IsAssignableFrom<IWorkerSessionLock>(
            await provider.TryAcquireAsync(
                WorkerJobs.UsageRebuild,
                cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask InvalidateCheckpointAsync(
        RebuildScenario scenario,
        bool replaceOwner,
        CancellationToken cancellationToken)
    {
        string sql = replaceOwner
            ? """
                UPDATE public.aggregation_watermarks
                SET lease_owner = 'concurrent-takeover',
                    lease_until = clock_timestamp() + interval '5 minutes',
                    version = version + 1,
                    updated_at = clock_timestamp()
                WHERE projector_name = 'usage-hourly-v1'
                  AND partition_key = $1
                  AND lease_owner IS NOT NULL;
                """
            : """
                UPDATE public.aggregation_watermarks
                SET lease_until = clock_timestamp() - interval '1 second',
                    updated_at = clock_timestamp()
                WHERE projector_name = 'usage-hourly-v1'
                  AND partition_key = $1
                  AND lease_owner IS NOT NULL;
                """;
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(Partition(scenario.GroupId));
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask TerminateJobLockSessionAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        FieldInfo leaseField = Assert.IsAssignableFrom<FieldInfo>(jobLock.GetType().GetField(
            "_lease",
            BindingFlags.Instance | BindingFlags.NonPublic));
        object technicalLease = Assert.IsAssignableFrom<object>(
            leaseField.GetValue(jobLock));
        PropertyInfo processIdProperty = Assert.IsAssignableFrom<PropertyInfo>(
            technicalLease.GetType().GetProperty(
                "BackendProcessId",
                BindingFlags.Instance | BindingFlags.NonPublic));
        int processId = Assert.IsType<int>(processIdProperty.GetValue(technicalLease));
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(
            "SELECT pg_catalog.pg_terminate_backend($1, 5000);");
        command.Parameters.AddWithValue(processId);
        Assert.True(Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private async ValueTask<RebuildScenario> SeedAsync(
        CancellationToken cancellationToken)
    {
        RebuildScenario scenario = RebuildScenario.Create();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await InsertBaseFactsAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        long checkpointSequence = await InsertQuotaEventChainAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        await InsertAdjustmentAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        await InsertDamagedProjectionAsync(
            connection,
            transaction,
            scenario,
            checkpointSequence,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return scenario with { CheckpointSequence = checkpointSequence };
    }

    private static async ValueTask<long> InsertQuotaEventChainAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        await InsertInitialAndReservationEventsAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        await InsertAdjustedSettlementEventsAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        return await InsertBaseReservationAndSettlementEventsAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertInitialAndReservationEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        _ = await InsertEventAsync(
            connection, transaction, EntityId.New(), scenario, null, null,
            "initialized", 1000, 0, 0, 1000, 0, 0,
            scenario.BucketStart.AddHours(-1), cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection, transaction, EntityId.New(), scenario,
            scenario.AdjustedReservationId, scenario.AdjustedAttemptId,
            "reserved", 0, 0, 15, 1000, 0, 15,
            scenario.BucketStart.AddMinutes(1), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertAdjustedSettlementEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        _ = await InsertEventAsync(
            connection, transaction, EntityId.New(), scenario,
            scenario.AdjustedReservationId, scenario.AdjustedAttemptId,
            "settled", 0, 15, -15, 1000, 15, 0,
            scenario.BucketStart.AddMinutes(11), cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection, transaction, scenario.AdjustmentEventId, scenario,
            scenario.AdjustedReservationId, scenario.AdjustedAttemptId,
            "usage_adjusted", 0, -3, 0, 1000, 12, 0,
            scenario.BucketStart.AddMinutes(12), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> InsertBaseReservationAndSettlementEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        _ = await InsertEventAsync(
            connection, transaction, EntityId.New(), scenario,
            scenario.BaseReservationId, scenario.BaseAttemptId,
            "reserved", 0, 0, 9, 1000, 12, 9,
            scenario.BucketStart.AddMinutes(13), cancellationToken).ConfigureAwait(false);
        return await InsertEventAsync(
            connection, transaction, EntityId.New(), scenario,
            scenario.BaseReservationId, scenario.BaseAttemptId,
            "settled", 0, 9, -9, 1000, 21, 0,
            scenario.BucketStart.AddMinutes(21), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask InsertBaseFactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        object[] values =
        [
            scenario.GroupId.Value,
            scenario.PeriodId.Value,
            scenario.RequestId.Value,
            scenario.AdjustedReservationId.Value,
            scenario.AdjustedAttemptId.Value,
            scenario.BaseReservationId.Value,
            scenario.BaseAttemptId.Value,
            scenario.AdjustedAccountId.Value,
            scenario.BaseAccountId.Value,
            scenario.StaleAccountId.Value,
            scenario.ChannelId.Value,
            scenario.BucketStart,
        ];
        await ExecuteStatementsAsync(
            connection,
            transaction,
            BaseFactsSql,
            values,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId eventId,
        RebuildScenario scenario,
        EntityId? reservationId,
        EntityId? attemptId,
        string eventType,
        long deltaTotal,
        long deltaConsumed,
        long deltaReserved,
        long totalAfter,
        long consumedAfter,
        long reservedAfter,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, reservation_id, attempt_id,
                event_type, delta_total_tokens, delta_consumed_tokens,
                delta_reserved_tokens, total_tokens_after,
                consumed_tokens_after, reserved_tokens_after, actor_type,
                idempotency_key, metadata, occurred_at)
            VALUES (
                $1, $2, $3, $4, $5,
                $6, $7, $8, $9, $10, $11, $12,
                'system', $13, '{}'::jsonb, $14)
            RETURNING event_sequence;
            """;
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = reservationId is { } reservation
                ? reservation.Value
                : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = attemptId is { } attempt ? attempt.Value : DBNull.Value,
        });
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(deltaTotal);
        command.Parameters.AddWithValue(deltaConsumed);
        command.Parameters.AddWithValue(deltaReserved);
        command.Parameters.AddWithValue(totalAfter);
        command.Parameters.AddWithValue(consumedAfter);
        command.Parameters.AddWithValue(reservedAfter);
        command.Parameters.AddWithValue($"bounded-rebuild:{eventId.Value:N}");
        command.Parameters.AddWithValue(occurredAt);
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask InsertAdjustmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.usage_attempt_adjustments (
                attempt_id, quota_event_id, previous_total_tokens,
                corrected_input_tokens, corrected_output_tokens,
                corrected_cache_read_tokens, corrected_cache_creation_tokens,
                corrected_thinking_tokens, usage_source, reason, adjusted_at)
            VALUES (
                $1, $2, 15, 8, 4, 1, 1, 2, 'upstream',
                'bounded rebuild checkpoint correction', $3);
            """;
        command.Parameters.AddWithValue(scenario.AdjustedAttemptId.Value);
        command.Parameters.AddWithValue(scenario.AdjustmentEventId.Value);
        command.Parameters.AddWithValue(scenario.BucketStart.AddMinutes(12));
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertDamagedProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        long checkpointSequence,
        CancellationToken cancellationToken)
    {
        object[] projectionValues =
        [
            scenario.GroupId.Value,
            scenario.PeriodId.Value,
            scenario.BucketStart,
            scenario.EmptyBucketStart,
            scenario.AdjustedAccountId.Value,
            scenario.BaseAccountId.Value,
            scenario.StaleAccountId.Value,
        ];
        await ExecuteStatementsAsync(
            connection,
            transaction,
            DamagedProjectionSql,
            projectionValues,
            cancellationToken).ConfigureAwait(false);

        long physicalSequence = await InsertSentinelOutboxAsync(
            connection,
            transaction,
            scenario,
            checkpointSequence,
            cancellationToken).ConfigureAwait(false);
        await InsertCheckpointAndInboxAsync(
            connection,
            transaction,
            scenario,
            checkpointSequence,
            physicalSequence,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> InsertSentinelOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        long checkpointSequence,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand envelope = connection.CreateCommand();
        envelope.Transaction = transaction;
        envelope.CommandText = SentinelOutboxSql;
        envelope.Parameters.AddWithValue(scenario.OutboxMessageId.Value);
        envelope.Parameters.AddWithValue(
            $"bounded-rebuild-sentinel:{scenario.OutboxMessageId.Value:N}");
        envelope.Parameters.AddWithValue(scenario.OutboxTopic);
        envelope.Parameters.AddWithValue(scenario.GroupId.Value);
        envelope.Parameters.AddWithValue(checkpointSequence);
        envelope.Parameters.AddWithValue(scenario.RequestId.Value);
        envelope.Parameters.AddWithValue(scenario.BucketStart.AddMinutes(30));
        return Assert.IsType<long>(await envelope
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask InsertCheckpointAndInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        long checkpointSequence,
        long physicalSequence,
        CancellationToken cancellationToken)
    {
        object[] checkpointValues =
        [
            Partition(scenario.GroupId),
            checkpointSequence,
            scenario.CompletedThrough,
            scenario.OutboxMessageId.Value,
            scenario.OutboxTopic,
            physicalSequence,
            SHA256.HashData("bounded-usage-rebuild-inbox"u8),
        ];
        await ExecuteStatementsAsync(
            connection,
            transaction,
            CheckpointAndInboxSql,
            checkpointValues,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteStatementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<object> parameterValues,
        CancellationToken cancellationToken)
    {
        foreach (string statement in sql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            foreach (object value in parameterValues.Take(ParameterCount(statement)))
            {
                command.Parameters.AddWithValue(value);
            }

            _ = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static int ParameterCount(string statement)
    {
        for (int candidate = 20; candidate > 0; candidate--)
        {
            if (statement.Contains($"${candidate}", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return 0;
    }

    private async ValueTask<ImmutableState> ReadImmutableStateAsync(
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(
            ImmutableStateSql);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        command.Parameters.AddWithValue(scenario.AdjustedAttemptId.Value);
        command.Parameters.AddWithValue(scenario.BaseAttemptId.Value);
        command.Parameters.AddWithValue(scenario.OutboxMessageId.Value);
        command.Parameters.AddWithValue(Partition(scenario.GroupId));
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ImmutableState state = new(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask<HourlyProjectionState?> ReadGroupProjectionAsync(
        RebuildScenario scenario,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT request_count, attempt_count, failure_count, failover_count,
                   estimated_attempt_count, input_tokens::text, output_tokens::text,
                   cache_creation_tokens::text, cache_read_tokens::text,
                   thinking_tokens::text, total_tokens::text
            FROM public.group_usage_hourly
            WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3;
            """);
        AddBucketParameters(command, scenario, bucketStart);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        HourlyProjectionState state = ReadHourlyState(reader, offset: 0);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask<IReadOnlyDictionary<EntityId, HourlyProjectionState>>
        ReadAccountProjectionsAsync(
            RebuildScenario scenario,
            DateTimeOffset bucketStart,
            CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT account_id, request_count, attempt_count, failure_count,
                   failover_count, estimated_attempt_count, input_tokens::text,
                   output_tokens::text, cache_creation_tokens::text,
                   cache_read_tokens::text, thinking_tokens::text,
                   total_tokens::text
            FROM public.account_usage_hourly
            WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3
            ORDER BY account_id;
            """);
        AddBucketParameters(command, scenario, bucketStart);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<EntityId, HourlyProjectionState> states = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            states.Add(new EntityId(reader.GetGuid(0)), ReadHourlyState(reader, offset: 1));
        }

        return states;
    }

    private async ValueTask<BigInteger> ReadGroupTotalAsync(
        RebuildScenario scenario,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken) => BigInteger.Parse(
            await ReadScalarTextAsync(
                """
                SELECT total_tokens::text
                FROM public.group_usage_hourly
                WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3;
                """,
                scenario,
                bucketStart,
                cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

    private async ValueTask<int> ReadAccountCountAsync(
        RebuildScenario scenario,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken) => int.Parse(
            await ReadScalarTextAsync(
                """
                SELECT count(*)::text
                FROM public.account_usage_hourly
                WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3;
                """,
                scenario,
                bucketStart,
                cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);

    private async ValueTask<bool> GroupBucketExistsAsync(
        RebuildScenario scenario,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM public.group_usage_hourly
                WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3);
            """);
        AddBucketParameters(command, scenario, bucketStart);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<string> ReadScalarTextAsync(
        string sql,
        RebuildScenario scenario,
        DateTimeOffset bucketStart,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand(sql);
        AddBucketParameters(command, scenario, bucketStart);
        return Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static HourlyProjectionState ReadHourlyState(
        NpgsqlDataReader reader,
        int offset) => new(
            reader.GetInt64(offset),
            reader.GetInt64(offset + 1),
            reader.GetInt64(offset + 2),
            reader.GetInt64(offset + 3),
            reader.GetInt64(offset + 4),
            reader.GetString(offset + 5),
            reader.GetString(offset + 6),
            reader.GetString(offset + 7),
            reader.GetString(offset + 8),
            reader.GetString(offset + 9),
            reader.GetString(offset + 10));

    private static void AddBucketParameters(
        NpgsqlCommand command,
        RebuildScenario scenario,
        DateTimeOffset bucketStart)
    {
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        command.Parameters.AddWithValue(bucketStart);
    }

    private static string Partition(EntityId groupId) =>
        $"poolai.quota.v1:group:{groupId.Value:D}";

    private static async ValueTask SetReplicaRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET LOCAL session_replication_role = replica;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SabotagingRebuildFactReader(
        IBoundedUsageRebuildFactReader inner,
        Func<CancellationToken, ValueTask> sabotage) :
        IBoundedUsageRebuildFactReader
    {
        private int _sabotaged;

        public async ValueTask<BoundedUsageRebuildHourSnapshot> ReadHourAsync(
            EntityId groupId,
            EntityId periodId,
            DateTimeOffset bucketStart,
            long checkpointSourceSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            BoundedUsageRebuildHourSnapshot snapshot = await inner.ReadHourAsync(
                groupId,
                periodId,
                bucketStart,
                checkpointSourceSequence,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _sabotaged, 1) == 0)
            {
                await sabotage(cancellationToken).ConfigureAwait(false);
            }

            return snapshot;
        }
    }

    private sealed class SabotagingProjectionWriter(
        IBoundedUsageProjectionWriter inner,
        Func<CancellationToken, ValueTask> sabotage) :
        IBoundedUsageProjectionWriter
    {
        private int _sabotaged;

        public async ValueTask ReplaceOrDeleteAsync(
            EntityId groupId,
            EntityId periodId,
            DateTimeOffset bucketStart,
            UsageHourProjection? projection,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            await inner.ReplaceOrDeleteAsync(
                groupId,
                periodId,
                bucketStart,
                projection,
                unitOfWorkContext,
                cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _sabotaged, 1) == 0)
            {
                await sabotage(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record ImmutableState(
        string ProtectedFacts,
        long LastEventSequence,
        DateTimeOffset CompletedThrough);

    private sealed record HourlyProjectionState(
        long RequestCount,
        long AttemptCount,
        long FailureCount,
        long FailoverCount,
        long EstimatedAttemptCount,
        string InputTokens,
        string OutputTokens,
        string CacheCreationTokens,
        string CacheReadTokens,
        string ThinkingTokens,
        string TotalTokens);

    private sealed record RebuildScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId RequestId,
        EntityId AdjustedReservationId,
        EntityId AdjustedAttemptId,
        EntityId BaseReservationId,
        EntityId BaseAttemptId,
        EntityId AdjustedAccountId,
        EntityId BaseAccountId,
        EntityId StaleAccountId,
        EntityId ChannelId,
        EntityId AdjustmentEventId,
        EntityId OutboxMessageId,
        string OutboxTopic,
        DateTimeOffset BucketStart,
        DateTimeOffset EmptyBucketStart,
        DateTimeOffset CompletedThrough,
        long CheckpointSequence)
    {
        internal static RebuildScenario Create()
        {
            EntityId outbox = EntityId.New();
            DateTimeOffset bucket = new(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
            return new RebuildScenario(
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                outbox,
                $"poolai.test.bounded-rebuild.{outbox.Value:N}",
                bucket,
                bucket.AddHours(1),
                bucket.AddMinutes(30),
                CheckpointSequence: 0);
        }
    }

    private static HourlyProjectionState ExpectedGroupProjection { get; } = new(
        RequestCount: 1,
        AttemptCount: 2,
        FailureCount: 1,
        FailoverCount: 1,
        EstimatedAttemptCount: 0,
        InputTokens: "14",
        OutputTokens: "7",
        CacheCreationTokens: "1",
        CacheReadTokens: "2",
        ThinkingTokens: "3",
        TotalTokens: "21");

    private static HourlyProjectionState ExpectedAdjustedAccountProjection { get; } = new(
        RequestCount: 1,
        AttemptCount: 1,
        FailureCount: 1,
        FailoverCount: 0,
        EstimatedAttemptCount: 0,
        InputTokens: "8",
        OutputTokens: "4",
        CacheCreationTokens: "1",
        CacheReadTokens: "1",
        ThinkingTokens: "2",
        TotalTokens: "12");

    private static HourlyProjectionState ExpectedBaseAccountProjection { get; } = new(
        RequestCount: 1,
        AttemptCount: 1,
        FailureCount: 0,
        FailoverCount: 1,
        EstimatedAttemptCount: 0,
        InputTokens: "6",
        OutputTokens: "3",
        CacheCreationTokens: "0",
        CacheReadTokens: "1",
        ThinkingTokens: "1",
        TotalTokens: "9");

    private const string SentinelOutboxSql = """
        INSERT INTO public.outbox_messages (
            id, deduplication_key, topic, schema_version,
            aggregate_type, aggregate_id, event_type,
            source_event_sequence, correlation_id, payload, occurred_at,
            status, next_attempt_at)
        VALUES (
            $1, $2, $3, 1, 'GroupQuota', $4, 'test_snapshot',
            $5, $6, '{"kind":"bounded-rebuild-sentinel"}'::jsonb,
            $7, 'pending', $7)
        RETURNING event_sequence;
        """;

    private const string CheckpointAndInboxSql = """
        INSERT INTO public.aggregation_watermarks (
            projector_name, partition_key, last_event_sequence,
            completed_through, version, updated_at)
        VALUES ('usage-hourly-v1', $1, $2, $3, 9, $3);

        INSERT INTO public.inbox_messages (
            consumer_name, message_id, topic, event_sequence,
            schema_version, payload_hash, processed_at)
        VALUES ('usage-hourly-v1', $4, $5, $6, 1, $7, $3);
        """;

    private const string ImmutableStateSql = """
        SELECT jsonb_build_object(
            'quota', (
                SELECT to_jsonb(quota)
                FROM public.group_token_quotas AS quota
                WHERE quota.group_id = $1),
            'period', (
                SELECT to_jsonb(period)
                FROM public.group_quota_periods AS period
                WHERE period.id = $2),
            'reservations', coalesce((
                SELECT jsonb_agg(to_jsonb(reservation) ORDER BY reservation.id::text)
                FROM public.group_token_reservations AS reservation
                WHERE reservation.group_id = $1
                  AND reservation.period_id = $2), '[]'::jsonb),
            'attempts', coalesce((
                SELECT jsonb_agg(to_jsonb(attempt) ORDER BY attempt.attempt_id::text)
                FROM public.usage_attempts AS attempt
                WHERE attempt.quota_group_id = $1), '[]'::jsonb),
            'adjustments', coalesce((
                SELECT jsonb_agg(to_jsonb(adjustment)
                    ORDER BY adjustment.attempt_id::text)
                FROM public.usage_attempt_adjustments AS adjustment
                WHERE adjustment.attempt_id IN ($3, $4)), '[]'::jsonb),
            'events', coalesce((
                SELECT jsonb_agg(to_jsonb(event) ORDER BY event.event_sequence)
                FROM public.group_quota_events AS event
                WHERE event.group_id = $1
                  AND event.period_id = $2), '[]'::jsonb),
            'outbox', (
                SELECT to_jsonb(outbox)
                FROM public.outbox_messages AS outbox
                WHERE outbox.id = $5),
            'inbox', (
                SELECT to_jsonb(inbox)
                FROM public.inbox_messages AS inbox
                WHERE inbox.consumer_name = 'usage-hourly-v1'
                  AND inbox.message_id = $5)
        )::text,
        watermark.last_event_sequence,
        watermark.completed_through
        FROM public.aggregation_watermarks AS watermark
        WHERE watermark.projector_name = 'usage-hourly-v1'
          AND watermark.partition_key = $6;
        """;

    private const string BaseFactsSql = """
        INSERT INTO public.groups (id, name, status, created_at, updated_at)
        VALUES ($1, 'bounded-period-rebuild-' || $1::text, 'disabled',
            $12 - interval '2 hours', $12 - interval '2 hours');

        INSERT INTO public.accounts (
            id, provider, name, auth_type, upstream_base_url,
            credential_envelope, credential_prefix, status)
        VALUES
            ($8, 'openai', 'bounded-account-' || $8::text, 'api_key',
                'https://fixture.invalid/v1', '{}'::jsonb, 'fixture', 'disabled'),
            ($9, 'openai', 'bounded-account-' || $9::text, 'api_key',
                'https://fixture.invalid/v1', '{}'::jsonb, 'fixture', 'disabled'),
            ($10, 'openai', 'bounded-account-' || $10::text, 'api_key',
                'https://fixture.invalid/v1', '{}'::jsonb, 'fixture', 'disabled');

        INSERT INTO public.group_token_quotas (
            group_id, current_period_id, enabled, version, created_at, updated_at)
        VALUES ($1, $2, true, 7, $12 - interval '2 hours', $12 + interval '21 minutes');

        INSERT INTO public.group_quota_periods (
            id, group_id, period_number, total_tokens, consumed_tokens,
            reserved_tokens, status, opened_at, version, created_at, updated_at)
        VALUES ($2, $1, 1, 1000, 21, 0, 'current',
            $12 - interval '1 hour', 7, $12 - interval '1 hour',
            $12 + interval '21 minutes');

        INSERT INTO public.usage_requests (
            request_id, user_id, api_key_id, subscription_id,
            quota_group_id, routing_group_id, endpoint, requested_model,
            effective_model, is_streaming, status, attempt_count,
            final_attempt_id, received_at, completed_at)
        VALUES ($3, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
            $1, $1, '/v1/responses', 'requested-model', 'upstream-model',
            false, 'succeeded', 2, $7, $12, $12 + interval '20 minutes');

        INSERT INTO public.group_token_reservations (
            id, period_id, group_id, request_id, attempt_id, attempt_index,
            account_id, channel_id, estimated_tokens, actual_tokens, status,
            is_streaming, lease_owner, lease_expires_at, max_expires_at,
            dispatch_started_at, dispatch_provider, dispatch_model,
            estimated_input_tokens, estimated_output_tokens, usage_source,
            settled_at, created_at, updated_at)
        VALUES
            ($4, $2, $1, $3, $5, 0, $8, $11, 15, 15, 'settled', false,
                'bounded-rebuild-test', $12 + interval '15 minutes',
                $12 + interval '30 minutes', $12 + interval '5 minutes',
                'openai', 'upstream-model', 10, 5, 'upstream',
                $12 + interval '10 minutes', $12, $12 + interval '10 minutes'),
            ($6, $2, $1, $3, $7, 1, $9, $11, 9, 9, 'settled', false,
                'bounded-rebuild-test', $12 + interval '25 minutes',
                $12 + interval '40 minutes', $12 + interval '15 minutes',
                'openai', 'upstream-model', 6, 3, 'upstream',
                $12 + interval '20 minutes', $12, $12 + interval '20 minutes');

        INSERT INTO public.usage_attempts (
            attempt_id, request_id, attempt_index, reservation_id,
            quota_group_id, routing_group_id, account_id, channel_id,
            provider, model, status, upstream_http_status,
            input_tokens, output_tokens, cache_read_tokens,
            cache_creation_tokens, thinking_tokens, usage_source,
            is_estimated, dispatch_started_at, first_token_at,
            completed_at, created_at)
        VALUES
            ($5, $3, 0, $4, $1, $1, $8, $11,
                'openai', 'upstream-model', 'failed', 500,
                10, 5, 2, 1, 3, 'upstream', false,
                $12 + interval '5 minutes', $12 + interval '6 minutes',
                $12 + interval '10 minutes', $12 + interval '10 minutes'),
            ($7, $3, 1, $6, $1, $1, $9, $11,
                'openai', 'upstream-model', 'succeeded', 200,
                6, 3, 1, 0, 1, 'upstream', false,
                $12 + interval '15 minutes', $12 + interval '16 minutes',
                $12 + interval '20 minutes', $12 + interval '20 minutes');
        """;

    private const string DamagedProjectionSql = """
        INSERT INTO public.group_usage_hourly (
            group_id, period_id, bucket_start, request_count, attempt_count,
            failure_count, failover_count, estimated_attempt_count,
            input_tokens, output_tokens, cache_creation_tokens,
            cache_read_tokens, thinking_tokens, total_tokens, version)
        VALUES
            ($1, $2, $3, 1, 2, 1, 1, 0, 666, 333, 4, 5, 6, 999, 3),
            ($1, $2, $4, 1, 1, 0, 0, 0, 4, 3, 0, 0, 0, 7, 3);

        INSERT INTO public.account_usage_hourly (
            group_id, account_id, period_id, bucket_start,
            request_count, attempt_count, failure_count, failover_count,
            estimated_attempt_count, input_tokens, output_tokens,
            cache_creation_tokens, cache_read_tokens, thinking_tokens,
            total_tokens, version)
        VALUES
            ($1, $5, $2, $3, 1, 1, 1, 0, 0, 500, 200, 1, 1, 1, 700, 3),
            ($1, $6, $2, $3, 1, 1, 0, 1, 0, 200, 99, 1, 1, 1, 299, 3),
            ($1, $7, $2, $3, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0, 1, 3),
            ($1, $7, $2, $4, 1, 1, 0, 0, 0, 4, 3, 0, 0, 0, 7, 3);
        """;
}
