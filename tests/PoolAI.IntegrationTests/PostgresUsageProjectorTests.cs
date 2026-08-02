using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresUsageProjectorTests
{
    private static long _physicalSequence = 4_000_000_000;
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresUsageProjectorTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FactsRebuildBucketsAndNoFactEventContradictionRollsBack()
    {
        // Governing contract: database README sections 8-9 and Proposed ADR 0012.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        IIntegrationEventConsumer consumer = ResolveConsumer();

        GroupQuotaEventFactSnapshot settledFact = await SeedQuotaEventAsync(
            scenario,
            "settled",
            deltaConsumed: 15,
            consumedAfter: 15,
            cancellationToken).ConfigureAwait(true);
        OutboxDeliveryMessage settled = Message(settledFact);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await consumer.ConsumeAsync(settled, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Duplicate,
            (await consumer.ConsumeAsync(settled, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            new ProjectionState(1, 1, "10", "5", "15", 1),
            await ReadGroupProjectionAsync(scenario, cancellationToken)
                .ConfigureAwait(true));

        GroupQuotaEventFactSnapshot adjustedFact = await SeedQuotaEventAsync(
            scenario,
            "usage_adjusted",
            deltaConsumed: -3,
            consumedAfter: 12,
            cancellationToken).ConfigureAwait(true);
        await SeedAdjustmentAsync(scenario, adjustedFact.EventId, cancellationToken)
            .ConfigureAwait(true);
        OutboxDeliveryMessage adjusted = Message(adjustedFact);
        Assert.Equal(
            IntegrationEventConsumeDisposition.Processed,
            (await consumer.ConsumeAsync(adjusted, cancellationToken)
                .ConfigureAwait(true)).Disposition);
        Assert.Equal(
            new ProjectionState(1, 1, "8", "4", "12", 2),
            await ReadGroupProjectionAsync(scenario, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(
            await ReadGroupProjectionAsync(scenario, cancellationToken).ConfigureAwait(true),
            await ReadAccountProjectionAsync(scenario, cancellationToken).ConfigureAwait(true));

        await AssertReleasedContradictionAsync(
            consumer,
            scenario,
            adjustedFact.SourceEventSequence,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FactReadersReturnNoFactForUnknownCanonicalIdentity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);
        PostgresGroupQuotaEventFactReader eventReader = new();
        PostgresAttemptSettlementHourFactReader settlementReader = new();

        Assert.Null(await eventReader.ReadAsync(
            EntityId.New(),
            sourceEventSequence: 1,
            session,
            cancellationToken).ConfigureAwait(true));
        Assert.Null(await settlementReader.ReadForAttemptAsync(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            session,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NullMetadataIsCanonicalizedExactlyLikeThePublishedPayload()
    {
        // Governing contract: ADR 0012 freezes one canonical event fact/payload identity.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        JsonElement storedMetadata = JsonSerializer.SerializeToElement(new
        {
            request_id = scenario.RequestId.ToString(),
            optional_evidence = (string?)null,
        });
        GroupQuotaEventFactSnapshot storedFact = await SeedQuotaEventAsync(
            scenario,
            "settled",
            deltaConsumed: 15,
            consumedAfter: 15,
            cancellationToken,
            storedMetadata).ConfigureAwait(true);
        JsonElement publishedMetadata = JsonSerializer.SerializeToElement(new
        {
            request_id = scenario.RequestId.ToString(),
        });

        IntegrationEventConsumeResult result = await ResolveConsumer().ConsumeAsync(
            Message(storedFact with { Metadata = publishedMetadata }),
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(IntegrationEventConsumeDisposition.Processed, result.Disposition);
        Assert.Equal(
            new ProjectionState(1, 1, "10", "5", "15", 1),
            await ReadGroupProjectionAsync(scenario, cancellationToken)
                .ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EventFactReaderAcceptsEveryFrozenEventType()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);
        PostgresGroupQuotaEventFactReader reader = new();
        string[] eventTypes =
        [
            "initialized",
            "reserved",
            "dispatch_started",
            "renewed",
            "settled",
            "released",
            "expired",
            "usage_adjusted",
            "total_adjusted",
            "period_reset",
        ];

        foreach (string eventType in eventTypes)
        {
            EntityId eventId = EntityId.New();
            JsonElement metadata = JsonSerializer.SerializeToElement(new
            {
                request_id = scenario.RequestId.ToString(),
            });
            long sequence = await InsertQuotaEventAsync(
                connection,
                transaction,
                scenario,
                eventId,
                eventType,
                deltaConsumed: 0,
                consumedAfter: 15,
                metadata,
                scenario.CompletedAt.AddMinutes(1),
                cancellationToken).ConfigureAwait(true);

            GroupQuotaEventFactSnapshot fact = Assert.IsType<GroupQuotaEventFactSnapshot>(
                await reader.ReadAsync(
                    scenario.GroupId,
                    sequence,
                    session,
                    cancellationToken).ConfigureAwait(true));
            Assert.Equal(eventType, fact.EventType);
            Assert.Equal(scenario.RequestId, fact.CorrelationId);
        }
    }

    [Theory]
    [InlineData("empty_event_id")]
    [InlineData("metadata_array")]
    [InlineData("request_id_number")]
    [InlineData("request_id_invalid")]
    [InlineData("unknown_event_type")]
    [InlineData("reference_mismatch")]
    [InlineData("total_zero")]
    [InlineData("total_overflow")]
    [InlineData("consumed_negative")]
    [InlineData("reserved_negative")]
    [Trait("Category", "PostgreSQL")]
    public async Task EventFactReaderFailsClosedForEveryPersistedAbiFamily(
        string corruption)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        GroupQuotaEventFactSnapshot fact = await SeedQuotaEventAsync(
            scenario,
            "settled",
            deltaConsumed: 15,
            consumedAfter: 15,
            cancellationToken).ConfigureAwait(true);
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(true);
        await CorruptQuotaEventAsync(
            connection,
            transaction,
            fact.EventId,
            corruption,
            cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => new PostgresGroupQuotaEventFactReader().ReadAsync(
                    scenario.GroupId,
                    fact.SourceEventSequence,
                    session,
                    cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Contains("quota-event", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [Trait("Category", "PostgreSQL")]
    public async Task EventFactReaderFallsBackToEventIdentityWithoutCorrelation(
        string representation)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        JsonElement metadata = string.Equals(
            representation,
            "missing",
            StringComparison.Ordinal)
            ? JsonSerializer.SerializeToElement(new { evidence = "none" })
            : JsonSerializer.SerializeToElement(new { request_id = string.Empty });
        GroupQuotaEventFactSnapshot expected = await SeedQuotaEventAsync(
            scenario,
            "settled",
            deltaConsumed: 15,
            consumedAfter: 15,
            cancellationToken,
            metadata).ConfigureAwait(true);
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        PostgresTransactionSession session = new(connection, transaction);

        GroupQuotaEventFactSnapshot actual = Assert.IsType<GroupQuotaEventFactSnapshot>(
            await new PostgresGroupQuotaEventFactReader().ReadAsync(
                scenario.GroupId,
                expected.SourceEventSequence,
                session,
                cancellationToken).ConfigureAwait(true));

        Assert.Equal(expected.EventId, actual.CorrelationId);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EventFactReaderRejectsDuplicateSourceIdentity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        GroupQuotaEventFactSnapshot fact = await SeedQuotaEventAsync(
            scenario,
            "settled",
            deltaConsumed: 15,
            consumedAfter: 15,
            cancellationToken).ConfigureAwait(true);
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        int inserted = 0;
        foreach (string statement in DuplicateQuotaEventSql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            if (statement.Contains("$1", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(fact.EventId.Value);
                command.Parameters.AddWithValue(EntityId.New().Value);
            }

            int affected = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true);
            if (statement.StartsWith("INSERT", StringComparison.Ordinal))
            {
                inserted += affected;
            }
        }
        Assert.Equal(1, inserted);
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => new PostgresGroupQuotaEventFactReader().ReadAsync(
                    scenario.GroupId,
                    fact.SourceEventSequence,
                    session,
                    cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(
            "The PostgreSQL quota-event fact query returned duplicate identities.",
            exception.Message);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SettlementHourReaderRejectsDuplicateTargetFact()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        GroupQuotaEventFactSnapshot adjustmentEvent = await SeedQuotaEventAsync(
            scenario,
            "usage_adjusted",
            deltaConsumed: -3,
            consumedAfter: 12,
            cancellationToken).ConfigureAwait(true);
        await SeedAdjustmentAsync(scenario, adjustmentEvent.EventId, cancellationToken)
            .ConfigureAwait(true);
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        int inserted = 0;
        foreach (string statement in DuplicateAdjustmentSql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            if (statement.Contains("$1", StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(scenario.AttemptId.Value);
            }

            int affected = await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true);
            if (statement.StartsWith("INSERT", StringComparison.Ordinal))
            {
                inserted += affected;
            }
        }
        Assert.Equal(1, inserted);
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => new PostgresAttemptSettlementHourFactReader()
                    .ReadForAttemptAsync(
                        scenario.GroupId,
                        scenario.PeriodId,
                        scenario.AttemptId,
                        session,
                        cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(
            "The PostgreSQL completion-hour fact snapshot duplicated its target.",
            exception.Message);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SettlementHourReaderRejectsSnapshotThatOmitsItsTarget()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UsageFactScenario scenario = UsageFactScenario.Create();
        await SeedSettledFactAsync(scenario, cancellationToken).ConfigureAwait(true);
        EntityId siblingRequestId = EntityId.New();
        EntityId siblingReservationId = EntityId.New();
        EntityId siblingAttemptId = EntityId.New();
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(true);
        await CloneSettledFactAsync(
            connection,
            transaction,
            scenario,
            siblingRequestId,
            siblingReservationId,
            siblingAttemptId,
            cancellationToken).ConfigureAwait(true);
        using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE public.group_token_reservations
                SET request_id = $2
                WHERE id = $1;
                """;
            command.Parameters.AddWithValue(scenario.ReservationId.Value);
            command.Parameters.AddWithValue(EntityId.New().Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(true));
        }
        PostgresTransactionSession session = new(connection, transaction);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => new PostgresAttemptSettlementHourFactReader()
                    .ReadForAttemptAsync(
                        scenario.GroupId,
                        scenario.PeriodId,
                        scenario.AttemptId,
                        session,
                        cancellationToken).AsTask()).ConfigureAwait(true);

        Assert.Equal(
            "The PostgreSQL completion-hour fact snapshot omitted its target.",
            exception.Message);
    }

    private IIntegrationEventConsumer ResolveConsumer() => Assert.Single(
        _fixture.WorkerServices.GetServices<IIntegrationEventConsumer>(),
        static consumer =>
            string.Equals(
                consumer.Subscription.Topic,
                GroupQuotaEventV1Codec.Topic,
                StringComparison.Ordinal)
            && consumer.Subscription.SchemaVersion == GroupQuotaEventV1Codec.SchemaVersion);

    private async ValueTask AssertReleasedContradictionAsync(
        IIntegrationEventConsumer consumer,
        UsageFactScenario scenario,
        long priorSourceEventSequence,
        CancellationToken cancellationToken)
    {
        GroupQuotaEventFactSnapshot releasedFact = await SeedQuotaEventAsync(
            scenario,
            "released",
            deltaConsumed: 0,
            consumedAfter: 12,
            cancellationToken).ConfigureAwait(false);
        JsonElement tamperedMetadata = JsonSerializer.SerializeToElement(new
        {
            request_id = scenario.RequestId.ToString(),
            tampered = true,
        });
        OutboxDeliveryMessage tampered = Message(releasedFact, tamperedMetadata);
        IntegrationEventConsumeResult ledgerPoison = await consumer.ConsumeAsync(
            tampered,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, ledgerPoison.Disposition);
        Assert.Equal("quota_event_fact_mismatch", ledgerPoison.Reason);

        OutboxDeliveryMessage released = Message(releasedFact);
        IntegrationEventConsumeResult factPoison = await consumer.ConsumeAsync(
            released,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(IntegrationEventConsumeDisposition.Poison, factPoison.Disposition);
        Assert.Equal("quota_fact_mismatch", factPoison.Reason);
        Assert.Equal(priorSourceEventSequence, await ReadWatermarkAsync(
            scenario,
            cancellationToken)
            .ConfigureAwait(false));
        Assert.False(await InboxExistsAsync(
            tampered.Envelope.Lease.MessageId,
            cancellationToken).ConfigureAwait(false));
        Assert.False(await InboxExistsAsync(
            released.Envelope.Lease.MessageId,
            cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask SeedSettledFactAsync(
        UsageFactScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetReplicaRoleAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        int affected = 0;
        foreach (string statement in SeedSettledFactSql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            AddSeedParameters(command, scenario, ParameterCount(statement));
            affected += await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Equal(7, affected);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SeedAdjustmentAsync(
        UsageFactScenario scenario,
        EntityId eventId,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.usage_attempt_adjustments (
                attempt_id, quota_event_id, previous_total_tokens,
                corrected_input_tokens, corrected_output_tokens,
                corrected_cache_read_tokens, corrected_cache_creation_tokens,
                corrected_thinking_tokens, usage_source, reason, adjusted_at
            ) VALUES (
                $1, $2, 15, 8, 4, 1, 1, 2,
                'upstream', 'integration correction', $3
            );
            """;
        command.Parameters.AddWithValue(scenario.AttemptId.Value);
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(scenario.CompletedAt.AddMinutes(2));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<GroupQuotaEventFactSnapshot> SeedQuotaEventAsync(
        UsageFactScenario scenario,
        string eventType,
        long deltaConsumed,
        long consumedAfter,
        CancellationToken cancellationToken,
        JsonElement? metadataOverride = null)
    {
        EntityId eventId = EntityId.New();
        DateTimeOffset occurredAt = scenario.CompletedAt.AddMinutes(
            string.Equals(eventType, "settled", StringComparison.Ordinal) ? 1 :
            string.Equals(eventType, "usage_adjusted", StringComparison.Ordinal) ? 2 : 3);
        JsonElement metadata = metadataOverride ?? JsonSerializer.SerializeToElement(new
        {
            request_id = scenario.RequestId.ToString(),
        });
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long sequence = await InsertQuotaEventAsync(
            connection,
            transaction,
            scenario,
            eventId,
            eventType,
            deltaConsumed,
            consumedAfter,
            metadata,
            occurredAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new GroupQuotaEventFactSnapshot(
            eventId,
            sequence,
            scenario.RequestId,
            scenario.AttemptId,
            scenario.GroupId,
            scenario.PeriodId,
            scenario.ReservationId,
            scenario.AttemptId,
            eventType,
            System.Numerics.BigInteger.Zero,
            new System.Numerics.BigInteger(deltaConsumed),
            System.Numerics.BigInteger.Zero,
            new System.Numerics.BigInteger(1000),
            new System.Numerics.BigInteger(consumedAfter),
            System.Numerics.BigInteger.Zero,
            occurredAt,
            metadata);
    }

    private static async ValueTask<long> InsertQuotaEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageFactScenario scenario,
        EntityId eventId,
        string eventType,
        long deltaConsumed,
        long consumedAfter,
        JsonElement metadata,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SeedQuotaEventSql;
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        command.Parameters.AddWithValue(scenario.ReservationId.Value);
        command.Parameters.AddWithValue(scenario.AttemptId.Value);
        command.Parameters.AddWithValue(eventType);
        command.Parameters.AddWithValue(new System.Numerics.BigInteger(deltaConsumed));
        command.Parameters.AddWithValue(new System.Numerics.BigInteger(consumedAfter));
        command.Parameters.AddWithValue($"usage-integration:{eventId.Value:N}");
        command.Parameters.AddWithValue(metadata.GetRawText());
        command.Parameters.AddWithValue(occurredAt);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

#pragma warning disable MA0051 // The test keeps each persisted ABI corruption explicit.
    private static async ValueTask CorruptQuotaEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId eventId,
        string corruption,
        CancellationToken cancellationToken)
    {
        (string Setup, string UpdateSql, object Value) plan = corruption switch
        {
            "empty_event_id" => (
                string.Empty,
                "UPDATE public.group_quota_events SET id = $2 WHERE id = $1;",
                Guid.Empty),
            "metadata_array" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_metadata;",
                "UPDATE public.group_quota_events "
                    + "SET metadata = $2::jsonb WHERE id = $1;",
                "[]"),
            "request_id_number" => (
                string.Empty,
                "UPDATE public.group_quota_events "
                    + "SET metadata = $2::jsonb WHERE id = $1;",
                "{\"request_id\":42}"),
            "request_id_invalid" => (
                string.Empty,
                "UPDATE public.group_quota_events "
                    + "SET metadata = $2::jsonb WHERE id = $1;",
                "{\"request_id\":\"not-a-uuid\"}"),
            "unknown_event_type" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_type;",
                "UPDATE public.group_quota_events "
                    + "SET event_type = $2 WHERE id = $1;",
                "unknown"),
            "reference_mismatch" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_reservation_identity;",
                "UPDATE public.group_quota_events "
                    + "SET reservation_id = $2 WHERE id = $1;",
                DBNull.Value),
            "total_zero" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_total_after;",
                "UPDATE public.group_quota_events "
                    + "SET total_tokens_after = $2 WHERE id = $1;",
                0m),
            "total_overflow" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_total_after;",
                "UPDATE public.group_quota_events "
                    + "SET total_tokens_after = $2 WHERE id = $1;",
                9_007_199_254_740_992m),
            "consumed_negative" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_consumed_after;",
                "UPDATE public.group_quota_events "
                    + "SET consumed_tokens_after = $2 WHERE id = $1;",
                -1m),
            "reserved_negative" => (
                "ALTER TABLE public.group_quota_events "
                    + "DROP CONSTRAINT ck_group_quota_events_reserved_after;",
                "UPDATE public.group_quota_events "
                    + "SET reserved_tokens_after = $2 WHERE id = $1;",
                -1m),
            _ => throw new InvalidOperationException("Unknown quota-event corruption."),
        };
        (string setup, string updateSql, object value) = plan;
        if (setup.Length > 0)
        {
            using NpgsqlCommand setupCommand = connection.CreateCommand();
            setupCommand.Transaction = transaction;
            setupCommand.CommandText = setup;
            _ = await setupCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = updateSql;
        command.Parameters.AddWithValue(eventId.Value);
        command.Parameters.AddWithValue(value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static async ValueTask CloneSettledFactAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UsageFactScenario scenario,
        EntityId requestId,
        EntityId reservationId,
        EntityId attemptId,
        CancellationToken cancellationToken)
    {
        const string cloneSql = """
            INSERT INTO public.usage_requests (
                request_id, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint, requested_model,
                effective_model, is_streaming, status, attempt_count,
                final_attempt_id, received_at, completed_at
            )
            SELECT
                $2, user_id, api_key_id, subscription_id,
                quota_group_id, routing_group_id, endpoint, requested_model,
                effective_model, is_streaming, status, attempt_count,
                $4, received_at, completed_at
            FROM public.usage_requests
            WHERE request_id = $1;

            INSERT INTO public.group_token_reservations (
                id, period_id, group_id, request_id, attempt_id, attempt_index,
                account_id, channel_id, estimated_tokens, actual_tokens, status,
                is_streaming, lease_owner, lease_expires_at, max_expires_at,
                dispatch_started_at, dispatch_provider, dispatch_model,
                estimated_input_tokens, estimated_output_tokens,
                usage_source, settled_at, created_at
            )
            SELECT
                $3, period_id, group_id, $2, $4, attempt_index,
                account_id, channel_id, estimated_tokens, actual_tokens, status,
                is_streaming, lease_owner, lease_expires_at, max_expires_at,
                dispatch_started_at, dispatch_provider, dispatch_model,
                estimated_input_tokens, estimated_output_tokens,
                usage_source, settled_at, created_at
            FROM public.group_token_reservations
            WHERE id = $5;

            INSERT INTO public.usage_attempts (
                attempt_id, request_id, attempt_index, reservation_id,
                quota_group_id, routing_group_id, account_id, channel_id,
                provider, model, status, upstream_http_status, error_code,
                input_tokens, output_tokens, cache_read_tokens,
                cache_creation_tokens, thinking_tokens, usage_source,
                is_estimated, upstream_request_id, raw_upstream_usage,
                dispatch_started_at, first_token_at, completed_at, created_at
            )
            SELECT
                $4, $2, attempt_index, $3,
                quota_group_id, routing_group_id, account_id, channel_id,
                provider, model, status, upstream_http_status, error_code,
                input_tokens, output_tokens, cache_read_tokens,
                cache_creation_tokens, thinking_tokens, usage_source,
                is_estimated, upstream_request_id, raw_upstream_usage,
                dispatch_started_at, first_token_at, completed_at, created_at
            FROM public.usage_attempts
            WHERE attempt_id = $6;
            """;
        int affected = 0;
        foreach (string statement in cloneSql.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.Parameters.AddWithValue(scenario.RequestId.Value);
            command.Parameters.AddWithValue(requestId.Value);
            command.Parameters.AddWithValue(reservationId.Value);
            command.Parameters.AddWithValue(attemptId.Value);
            command.Parameters.AddWithValue(scenario.ReservationId.Value);
            command.Parameters.AddWithValue(scenario.AttemptId.Value);
            affected += await command.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Equal(3, affected);
    }
#pragma warning restore MA0051

    private static async ValueTask SetReplicaRoleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SET LOCAL session_replication_role = replica;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddSeedParameters(
        NpgsqlCommand command,
        UsageFactScenario scenario,
        int parameterCount)
    {
        object[] values =
        [
            scenario.GroupId.Value,
            scenario.PeriodId.Value,
            scenario.AccountId.Value,
            scenario.RequestId.Value,
            scenario.ReservationId.Value,
            scenario.AttemptId.Value,
            scenario.ChannelId.Value,
            scenario.CompletedAt.AddMinutes(-2),
            scenario.CompletedAt.AddSeconds(-30),
            scenario.CompletedAt,
        ];
        foreach (object value in values.Take(parameterCount))
        {
            command.Parameters.AddWithValue(value);
        }
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

    private async ValueTask<ProjectionState> ReadGroupProjectionAsync(
        UsageFactScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT request_count, attempt_count,
                   input_tokens::text, output_tokens::text, total_tokens::text, version
            FROM public.group_usage_hourly
            WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3;
            """);
        AddProjectionKey(command, scenario);
        return await ReadProjectionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectionState> ReadAccountProjectionAsync(
        UsageFactScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT request_count, attempt_count,
                   input_tokens::text, output_tokens::text, total_tokens::text, version
            FROM public.account_usage_hourly
            WHERE group_id = $1 AND period_id = $2 AND bucket_start = $3
              AND account_id = $4;
            """);
        AddProjectionKey(command, scenario);
        command.Parameters.AddWithValue(scenario.AccountId.Value);
        return await ReadProjectionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void AddProjectionKey(NpgsqlCommand command, UsageFactScenario scenario)
    {
        command.Parameters.AddWithValue(scenario.GroupId.Value);
        command.Parameters.AddWithValue(scenario.PeriodId.Value);
        command.Parameters.AddWithValue(scenario.BucketStart);
    }

    private static async ValueTask<ProjectionState> ReadProjectionAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ProjectionState state = new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return state;
    }

    private async ValueTask<long> ReadWatermarkAsync(
        UsageFactScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT last_event_sequence
            FROM public.aggregation_watermarks
            WHERE projector_name = 'usage-hourly-v1' AND partition_key = $1;
            """);
        command.Parameters.AddWithValue(Partition(scenario.GroupId));
        return Assert.IsType<long>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<bool> InboxExistsAsync(
        EntityId messageId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM public.inbox_messages
                WHERE consumer_name = 'usage-hourly-v1' AND message_id = $1
            );
            """);
        command.Parameters.AddWithValue(messageId.Value);
        return Assert.IsType<bool>(await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static OutboxDeliveryMessage Message(
        GroupQuotaEventFactSnapshot fact,
        JsonElement? metadataOverride = null)
    {
        EntityId messageId = EntityId.New();
        JsonElement payload = Payload(fact, metadataOverride);
        OutboxMessageEnvelope envelope = new(
            new OutboxDeliveryLease(messageId, EntityId.New(), Generation: 1, Attempt: 1),
            Interlocked.Increment(ref _physicalSequence),
            $"quota:integration:{messageId.Value:N}",
            GroupQuotaEventV1Codec.Topic,
            GroupQuotaEventV1Codec.SchemaVersion,
            GroupQuotaEventV1Codec.AggregateType,
            fact.GroupId,
            AggregateVersion: null,
            fact.EventType,
            fact.SourceEventSequence,
            fact.CorrelationId,
            fact.CausationId,
            payload,
            fact.OccurredAt,
            ReplayOf: null);
        return new OutboxDeliveryMessage(
            envelope,
            Partition(fact.GroupId),
            fact.SourceEventSequence);
    }

    private static JsonElement Payload(
        GroupQuotaEventFactSnapshot fact,
        JsonElement? metadataOverride) => JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            event_id = fact.EventId.ToString(),
            source_event_sequence = fact.SourceEventSequence,
            correlation_id = fact.CorrelationId.ToString(),
            causation_id = fact.CausationId?.ToString(),
            group_id = fact.GroupId.ToString(),
            period_id = fact.PeriodId.ToString(),
            reservation_id = fact.ReservationId?.ToString(),
            attempt_id = fact.AttemptId?.ToString(),
            event_type = fact.EventType,
            delta_total_tokens = fact.DeltaTotalTokens.ToString(CultureInfo.InvariantCulture),
            delta_consumed_tokens = fact.DeltaConsumedTokens.ToString(
                CultureInfo.InvariantCulture),
            delta_reserved_tokens = fact.DeltaReservedTokens.ToString(
                CultureInfo.InvariantCulture),
            total_tokens = fact.TotalTokens.ToString(CultureInfo.InvariantCulture),
            consumed_tokens = fact.ConsumedTokens.ToString(CultureInfo.InvariantCulture),
            reserved_tokens = fact.ReservedTokens.ToString(CultureInfo.InvariantCulture),
            occurred_at = fact.OccurredAt,
            metadata = metadataOverride ?? fact.Metadata,
        });

    private static string Partition(EntityId groupId) =>
        $"{GroupQuotaEventV1Codec.Topic}:group:{groupId}";

    private sealed record ProjectionState(
        long RequestCount,
        long AttemptCount,
        string InputTokens,
        string OutputTokens,
        string TotalTokens,
        long Version);

    private sealed record UsageFactScenario(
        EntityId GroupId,
        EntityId PeriodId,
        EntityId AccountId,
        EntityId RequestId,
        EntityId ReservationId,
        EntityId AttemptId,
        EntityId ChannelId,
        DateTimeOffset BucketStart,
        DateTimeOffset CompletedAt)
    {
        internal static UsageFactScenario Create()
        {
            DateTimeOffset bucket = new(2026, 8, 2, 6, 0, 0, TimeSpan.Zero);
            return new UsageFactScenario(
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                bucket,
                bucket.AddMinutes(15));
        }
    }

    private const string SeedSettledFactSql = """
        INSERT INTO public.groups (id, name, status)
        VALUES ($1, 'usage-projector-' || $1::text, 'disabled');
        INSERT INTO public.accounts (
            id, provider, name, auth_type, upstream_base_url,
            credential_envelope, credential_prefix, status
        ) VALUES (
            $3, 'openai', 'usage-account-' || $3::text, 'api_key',
            'https://fixture.invalid/v1', '{}'::jsonb, 'fixture', 'disabled'
        );
        INSERT INTO public.group_token_quotas (group_id, current_period_id)
        VALUES ($1, $2);
        INSERT INTO public.group_quota_periods (
            id, group_id, period_number, total_tokens,
            consumed_tokens, reserved_tokens, status, opened_at
        ) VALUES ($2, $1, 1, 1000, 15, 0, 'current', $8);
        INSERT INTO public.usage_requests (
            request_id, user_id, api_key_id, subscription_id,
            quota_group_id, routing_group_id, endpoint, requested_model,
            effective_model, is_streaming, status, attempt_count,
            final_attempt_id, received_at, completed_at
        ) VALUES (
            $4, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(),
            $1, $1, '/v1/responses', 'requested-model', 'upstream-model',
            false, 'succeeded', 1, $6, $8, $10
        );
        INSERT INTO public.group_token_reservations (
            id, period_id, group_id, request_id, attempt_id, attempt_index,
            account_id, channel_id, estimated_tokens, actual_tokens, status,
            is_streaming, lease_owner, lease_expires_at, max_expires_at,
            dispatch_started_at, dispatch_provider, dispatch_model,
            estimated_input_tokens, estimated_output_tokens,
            usage_source, settled_at, created_at
        ) VALUES (
            $5, $2, $1, $4, $6, 0, $3, $7, 15, 15, 'settled',
            false, 'integration-owner', $10 + interval '5 minutes',
            $10 + interval '10 minutes', $9, 'openai', 'upstream-model',
            10, 5, 'upstream', $10, $8
        );
        INSERT INTO public.usage_attempts (
            attempt_id, request_id, attempt_index, reservation_id,
            quota_group_id, routing_group_id, account_id, channel_id,
            provider, model, status, upstream_http_status,
            input_tokens, output_tokens, cache_read_tokens,
            cache_creation_tokens, thinking_tokens, usage_source,
            is_estimated, dispatch_started_at, completed_at
        ) VALUES (
            $6, $4, 0, $5, $1, $1, $3, $7,
            'openai', 'upstream-model', 'succeeded', 200,
            10, 5, 2, 1, 3, 'upstream', false, $9, $10
        );
        """;

    private const string SeedQuotaEventSql = """
        INSERT INTO public.group_quota_events (
            id, group_id, period_id, reservation_id, attempt_id, event_type,
            delta_total_tokens, delta_consumed_tokens, delta_reserved_tokens,
            total_tokens_after, consumed_tokens_after, reserved_tokens_after,
            actor_type, idempotency_key, metadata, occurred_at
        ) VALUES (
            $1, $2, $3, $4, $5, $6,
            0, $7, 0, 1000, $8, 0,
            'worker', $9, $10::jsonb, $11
        )
        RETURNING event_sequence;
        """;

    private const string DuplicateQuotaEventSql = """
        ALTER TABLE public.group_quota_events
            DROP CONSTRAINT uq_group_quota_events_sequence;
        INSERT INTO public.group_quota_events (
            id, event_sequence, group_id, period_id, reservation_id, attempt_id,
            event_type, delta_total_tokens, delta_consumed_tokens,
            delta_reserved_tokens, total_tokens_after, consumed_tokens_after,
            reserved_tokens_after, actor_type, actor_user_id, idempotency_key,
            reason, metadata, occurred_at
        ) OVERRIDING SYSTEM VALUE
        SELECT
            $2, event_sequence, group_id, period_id, reservation_id, attempt_id,
            event_type, delta_total_tokens, delta_consumed_tokens,
            delta_reserved_tokens, total_tokens_after, consumed_tokens_after,
            reserved_tokens_after, actor_type, actor_user_id,
            idempotency_key || ':duplicate', reason, metadata, occurred_at
        FROM public.group_quota_events
        WHERE id = $1;
        """;

    private const string DuplicateAdjustmentSql = """
        ALTER TABLE public.usage_attempt_adjustments
            DROP CONSTRAINT usage_attempt_adjustments_pkey;
        ALTER TABLE public.usage_attempt_adjustments
            DROP CONSTRAINT uq_usage_attempt_adjustments_event;
        INSERT INTO public.usage_attempt_adjustments (
            attempt_id, quota_event_id, previous_total_tokens,
            corrected_input_tokens, corrected_output_tokens,
            corrected_cache_read_tokens, corrected_cache_creation_tokens,
            corrected_thinking_tokens, usage_source, reason,
            raw_upstream_usage, adjusted_at
        )
        SELECT
            attempt_id, quota_event_id, previous_total_tokens,
            corrected_input_tokens, corrected_output_tokens,
            corrected_cache_read_tokens, corrected_cache_creation_tokens,
            corrected_thinking_tokens, usage_source, reason,
            raw_upstream_usage, adjusted_at
        FROM public.usage_attempt_adjustments
        WHERE attempt_id = $1;
        """;
}
