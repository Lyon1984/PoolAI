#pragma warning disable MA0051 // The PostgreSQL checkpoint scenario keeps exact fact/event boundaries visible.
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresBoundedUsageRebuildFactReaderTests(
    PostgresRuntimeFixture fixture)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderAppliesExactHourGroupPeriodAndCheckpointFiltering()
    {
        // Governing contract: ADR 0013 bounded projection rebuild recovery boundary.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);

        BoundedUsageRebuildHourSnapshot beforeTerminal = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart,
            scenario.TerminalSequence - 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Empty(beforeTerminal.Facts);

        BoundedUsageRebuildHourSnapshot baseline = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart,
            scenario.TerminalSequence,
            cancellationToken).ConfigureAwait(true);
        AttemptSettlementFact baselineFact = Assert.Single(baseline.Facts);
        Assert.Equal(scenario.AttemptId, baselineFact.AttemptId);
        Assert.Equal(new BigInteger(15), baselineFact.Usage.Tokens.TotalTokens);
        Assert.Null(baselineFact.Adjustment);

        BoundedUsageRebuildHourSnapshot adjusted = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart,
            scenario.AdjustmentSequence,
            cancellationToken).ConfigureAwait(true);
        AttemptSettlementFact adjustedFact = Assert.Single(adjusted.Facts);
        Assert.Equal(new BigInteger(15), adjustedFact.Usage.Tokens.TotalTokens);
        Assert.Equal(
            new BigInteger(12),
            Assert.IsType<AttemptUsageAdjustment>(adjustedFact.Adjustment)
                .CorrectedTokens.TotalTokens);

        BoundedUsageRebuildHourSnapshot caughtUp = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart,
            scenario.LateTerminalSequence,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(2, caughtUp.Facts.Count);
        Assert.Contains(caughtUp.Facts, fact => fact.AttemptId == scenario.AttemptId);
        Assert.Contains(caughtUp.Facts, fact => fact.AttemptId == scenario.LateAttemptId);
        Assert.DoesNotContain(
            caughtUp.Facts,
            fact => fact.AttemptId == scenario.NextHourAttemptId);
        Assert.DoesNotContain(
            caughtUp.Facts,
            fact => fact.AttemptId == scenario.OtherGroupAttemptId);
        BoundedUsageRebuildHourSnapshot workerRead = await ReadAsync(
            fixture.WorkerServices,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart,
            scenario.LateTerminalSequence,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(caughtUp.Facts, workerRead.Facts);

        BoundedUsageRebuildHourSnapshot nextHour = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart.AddHours(1),
            scenario.LateTerminalSequence,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            scenario.NextHourAttemptId,
            Assert.Single(nextHour.Facts).AttemptId);

        BoundedUsageRebuildHourSnapshot otherGroup = await ReadAsync(
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            scenario.BucketStart,
            scenario.LateTerminalSequence,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            scenario.OtherGroupAttemptId,
            Assert.Single(otherGroup.Facts).AttemptId);

        BoundedUsageRebuildHourSnapshot empty = await ReadAsync(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.BucketStart.AddHours(2),
            scenario.LateTerminalSequence,
            cancellationToken).ConfigureAwait(true);
        Assert.Empty(empty.Facts);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderFailsClosedForDuplicateTerminalEvents()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        long duplicateSequence = await InsertAdditionalEventAsync(
            connection,
            transaction,
            scenario.TerminalEventId,
            eventType: "settled",
            deltaConsumedTokens: 15,
            cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => new PostgresBoundedUsageRebuildFactReader()
                .ReadHourAsync(
                    scenario.GroupId,
                    scenario.PeriodId,
                    scenario.BucketStart,
                    duplicateSequence,
                    session,
                    cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(
            "The PostgreSQL bounded rebuild fact query returned duplicate terminal events.",
            exception.Message);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderFailsClosedWhenAdjustmentEventViolatesItsAbi()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(true);
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE public.group_quota_events
                SET event_type = 'released'
                WHERE id = $1;
                """;
            command.Parameters.AddWithValue(scenario.AdjustmentEventId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true));
        }

        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => new PostgresBoundedUsageRebuildFactReader()
                .ReadHourAsync(
                    scenario.GroupId,
                    scenario.PeriodId,
                    scenario.BucketStart,
                    scenario.LateTerminalSequence,
                    session,
                    cancellationToken).AsTask()).ConfigureAwait(true);
        Assert.Equal(
            "The PostgreSQL bounded rebuild fact violated its ABI.",
            exception.Message);
    }

    private async ValueTask<BoundedUsageRebuildHourSnapshot> ReadAsync(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        long checkpointSourceSequence,
        CancellationToken cancellationToken) => await ReadAsync(
            fixture.ApiServices,
            groupId,
            periodId,
            bucketStart,
            checkpointSourceSequence,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<BoundedUsageRebuildHourSnapshot> ReadAsync(
        IServiceProvider services,
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        long checkpointSourceSequence,
        CancellationToken cancellationToken)
    {
        IBoundedUsageRebuildFactReader reader = services
            .GetRequiredService<IBoundedUsageRebuildFactReader>();
        IUnitOfWorkFactory factory = services
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        BoundedUsageRebuildHourSnapshot snapshot = await reader.ReadHourAsync(
            groupId,
            periodId,
            bucketStart,
            checkpointSourceSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<SeededScenario> SeedAsync(
        CancellationToken cancellationToken)
    {
        RebuildScenario scenario = RebuildScenario.Create();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await InsertGroupsAndPeriodsAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        foreach (AttemptSeed attempt in scenario.Attempts)
        {
            await InsertAttemptAsync(
                connection,
                transaction,
                attempt,
                cancellationToken).ConfigureAwait(false);
        }

        long terminalSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.TerminalEventId,
            scenario.Primary,
            eventType: "settled",
            deltaConsumedTokens: 15,
            cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection,
            transaction,
            EntityId.New(),
            scenario.OtherGroup,
            eventType: "settled",
            deltaConsumedTokens: 10,
            cancellationToken).ConfigureAwait(false);
        _ = await InsertEventAsync(
            connection,
            transaction,
            EntityId.New(),
            scenario.NextHour,
            eventType: "settled",
            deltaConsumedTokens: 2,
            cancellationToken).ConfigureAwait(false);
        long adjustmentSequence = await InsertEventAsync(
            connection,
            transaction,
            scenario.AdjustmentEventId,
            scenario.Primary,
            eventType: "usage_adjusted",
            deltaConsumedTokens: -3,
            cancellationToken).ConfigureAwait(false);
        await InsertAdjustmentAsync(
            connection,
            transaction,
            scenario,
            cancellationToken).ConfigureAwait(false);
        long lateTerminalSequence = await InsertEventAsync(
            connection,
            transaction,
            EntityId.New(),
            scenario.Late,
            eventType: "settled",
            deltaConsumedTokens: 6,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededScenario(
            scenario.GroupId,
            scenario.PeriodId,
            scenario.OtherGroupId,
            scenario.OtherPeriodId,
            scenario.Primary.AttemptId,
            scenario.Late.AttemptId,
            scenario.NextHour.AttemptId,
            scenario.OtherGroup.AttemptId,
            scenario.TerminalEventId,
            scenario.AdjustmentEventId,
            scenario.BucketStart,
            terminalSequence,
            adjustmentSequence,
            lateTerminalSequence);
    }

    private static async ValueTask InsertGroupsAndPeriodsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RebuildScenario scenario,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.groups (id, name, status)
            VALUES
                ($1, 'bounded-rebuild-' || $1::text, 'disabled'),
                ($3, 'bounded-rebuild-' || $3::text, 'disabled');

            INSERT INTO public.group_token_quotas (group_id, current_period_id)
            VALUES ($1, $2), ($3, $4);

            INSERT INTO public.group_quota_periods (
                id, group_id, period_number, total_tokens,
                consumed_tokens, reserved_tokens, status, opened_at)
            VALUES
                ($2, $1, 1, 1000, 23, 0, 'current', $5),
                ($4, $3, 1, 1000, 10, 0, 'current', $5);
            """;
        object[] parameters =
        [
            scenario.GroupId.Value,
            scenario.PeriodId.Value,
            scenario.OtherGroupId.Value,
            scenario.OtherPeriodId.Value,
            scenario.BucketStart.AddDays(-1),
        ];
        int affected = await ExecuteStatementsAsync(
            connection,
            transaction,
            sql,
            parameters,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(6, affected);
    }

    private static async ValueTask InsertAttemptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AttemptSeed attempt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO public.usage_requests (
                request_id, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint, requested_model,
                effective_model, is_streaming, status, attempt_count,
                final_attempt_id, received_at, completed_at)
            VALUES (
                $3, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
                $1, $1, '/v1/responses', 'requested-model', 'upstream-model',
                false, 'succeeded', 1, $5, $8 - interval '2 minutes', $8);

            INSERT INTO public.group_token_reservations (
                id, period_id, group_id, request_id, attempt_id, attempt_index,
                account_id, channel_id, estimated_tokens, actual_tokens, status,
                is_streaming, lease_owner, lease_expires_at, max_expires_at,
                dispatch_started_at, dispatch_provider, dispatch_model,
                estimated_input_tokens, estimated_output_tokens,
                usage_source, settled_at, created_at)
            VALUES (
                $4, $2, $1, $3, $5, 0, $6, $7, $9 + $10, $9 + $10,
                'settled', false, 'bounded-rebuild-test',
                $8 + interval '5 minutes', $8 + interval '10 minutes',
                $8 - interval '1 minute', 'openai', 'upstream-model',
                $9, $10, 'upstream', $8, $8 - interval '2 minutes');

            INSERT INTO public.usage_attempts (
                attempt_id, request_id, attempt_index, reservation_id,
                quota_group_id, routing_group_id, account_id, channel_id,
                provider, model, status, upstream_http_status,
                input_tokens, output_tokens, cache_read_tokens,
                cache_creation_tokens, thinking_tokens, usage_source,
                is_estimated, dispatch_started_at, completed_at)
            VALUES (
                $5, $3, 0, $4, $1, $1, $6, $7,
                'openai', 'upstream-model', 'succeeded', 200,
                $9, $10, 0, 0, 0, 'upstream', false,
                $8 - interval '1 minute', $8);
            """;
        object[] parameters =
        [
            attempt.GroupId.Value,
            attempt.PeriodId.Value,
            attempt.RequestId.Value,
            attempt.ReservationId.Value,
            attempt.AttemptId.Value,
            attempt.AccountId.Value,
            attempt.ChannelId.Value,
            attempt.CompletedAt,
            attempt.InputTokens,
            attempt.OutputTokens,
        ];
        int affected = await ExecuteStatementsAsync(
            connection,
            transaction,
            sql,
            parameters,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(3, affected);
    }

    private static async ValueTask<int> ExecuteStatementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        IReadOnlyList<object> parameterValues,
        CancellationToken cancellationToken)
    {
        int affected = 0;
        foreach (string statement in sql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            int parameterCount = ParameterCount(statement);
            foreach (object value in parameterValues.Take(parameterCount))
            {
                command.Parameters.AddWithValue(value);
            }

            affected += await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return affected;
    }

    private static int ParameterCount(string statement)
    {
        for (int candidate = 10; candidate > 0; candidate--)
        {
            if (statement.Contains($"${candidate}", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return 0;
    }

    private static async ValueTask<long> InsertEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId eventId,
        AttemptSeed attempt,
        string eventType,
        long deltaConsumedTokens,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, reservation_id, attempt_id, event_type,
                delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
                total_tokens_after, consumed_tokens_after, reserved_tokens_after,
                actor_type, idempotency_key, metadata, occurred_at)
            VALUES (
                $1, $2, $3, $4, $5, $6,
                0, $7, 0, 1000, 0, 0,
                'worker', 'bounded-rebuild:' || $1::text, '{}'::jsonb, $8)
            RETURNING event_sequence;
            """;
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(attempt.GroupId.Value);
        command.Parameters.AddWithValue(attempt.PeriodId.Value);
        command.Parameters.AddWithValue(attempt.ReservationId.Value);
        command.Parameters.AddWithValue(attempt.AttemptId.Value);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(new BigInteger(deltaConsumedTokens));
        command.Parameters.AddWithValue(attempt.CompletedAt.AddMinutes(1));
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
                $1, $2, 15, 8, 4, 0, 0, 0,
                'upstream', 'bounded rebuild correction', $3);
            """;
        command.Parameters.AddWithValue(scenario.Primary.AttemptId.Value);
        command.Parameters.AddWithValue(scenario.AdjustmentEventId.Value);
        command.Parameters.AddWithValue(scenario.Primary.CompletedAt.AddMinutes(2));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask<long> InsertAdditionalEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId priorEventId,
        string eventType,
        long deltaConsumedTokens,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.group_quota_events (
                id, group_id, period_id, reservation_id, attempt_id, event_type,
                delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
                total_tokens_after, consumed_tokens_after, reserved_tokens_after,
                actor_type, idempotency_key, metadata, occurred_at)
            SELECT
                $2, group_id, period_id, reservation_id, attempt_id, $3,
                0, $4, 0, total_tokens_after, consumed_tokens_after,
                reserved_tokens_after, actor_type,
                'bounded-rebuild-duplicate:' || $2::text,
                metadata, occurred_at + interval '1 minute'
            FROM public.group_quota_events
            WHERE id = $1
            RETURNING event_sequence;
            """;
        command.Parameters.AddWithValue(priorEventId.Value);
        command.Parameters.AddWithValue(EntityId.New().Value);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(new BigInteger(deltaConsumedTokens));
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

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

    private sealed record SeededScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId OtherGroupId,
        EntityId OtherPeriodId,
        EntityId AttemptId,
        EntityId LateAttemptId,
        EntityId NextHourAttemptId,
        EntityId OtherGroupAttemptId,
        EntityId TerminalEventId,
        EntityId AdjustmentEventId,
        DateTimeOffset BucketStart,
        long TerminalSequence,
        long AdjustmentSequence,
        long LateTerminalSequence);

    private sealed record AttemptSeed(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId RequestId,
        EntityId ReservationId,
        EntityId AttemptId,
        EntityId AccountId,
        EntityId ChannelId,
        DateTimeOffset CompletedAt,
        BigInteger InputTokens,
        BigInteger OutputTokens);

    private sealed record RebuildScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId OtherGroupId,
        EntityId OtherPeriodId,
        EntityId TerminalEventId,
        EntityId AdjustmentEventId,
        DateTimeOffset BucketStart,
        AttemptSeed Primary,
        AttemptSeed Late,
        AttemptSeed NextHour,
        AttemptSeed OtherGroup)
    {
        internal IReadOnlyList<AttemptSeed> Attempts =>
            [Primary, Late, NextHour, OtherGroup];

        internal static RebuildScenario Create()
        {
            EntityId groupId = EntityId.New();
            EntityId periodId = EntityId.New();
            EntityId otherGroupId = EntityId.New();
            EntityId otherPeriodId = EntityId.New();
            DateTimeOffset bucketStart = new(
                2030,
                1,
                2,
                3,
                0,
                0,
                TimeSpan.Zero);
            return new RebuildScenario(
                groupId,
                periodId,
                otherGroupId,
                otherPeriodId,
                EntityId.New(),
                EntityId.New(),
                bucketStart,
                CreateAttempt(groupId, periodId, bucketStart, 10, 5),
                CreateAttempt(
                    groupId,
                    periodId,
                    bucketStart.AddHours(1).AddTicks(-1),
                    4,
                    2),
                CreateAttempt(groupId, periodId, bucketStart.AddHours(1), 1, 1),
                CreateAttempt(
                    otherGroupId,
                    otherPeriodId,
                    bucketStart.AddMinutes(30),
                    7,
                    3));
        }

        private static AttemptSeed CreateAttempt(
            EntityId groupId,
            EntityId periodId,
            DateTimeOffset completedAt,
            long inputTokens,
            long outputTokens) => new(
                groupId,
                periodId,
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                completedAt,
                new BigInteger(inputTokens),
                new BigInteger(outputTokens));
    }
}
#pragma warning restore MA0051
