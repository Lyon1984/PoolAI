using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresQuotaDeliveryHealthReaderTests(
    PostgresRuntimeFixture fixture)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderAggregatesBoundedQuotaMessagesByLogicalLineage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SeededScenario scenario = await SeedAsync(cancellationToken).ConfigureAwait(true);

        QuotaDeliveryHealthSnapshot snapshot = await ReadAsync(
            scenario.GroupId,
            scenario.ExpectedSourceEventSequences,
            cancellationToken).ConfigureAwait(true);

        Assert.Equal(5, snapshot.OriginalCount);
        Assert.Equal(1, snapshot.MissingOriginalCount);
        Assert.Equal(0, snapshot.DuplicateOriginalCount);
        Assert.Equal(1, snapshot.PendingLineageCount);
        Assert.Equal(1, snapshot.ProcessingLineageCount);
        Assert.Equal(1, snapshot.DeadLineageCount);
        Assert.InRange(snapshot.OldestUnresolvedAgeSeconds, 240, 900);
        Assert.Equal(
            scenario.ExpectedSourceEventSequences[1],
            snapshot.BlockingSourceEventSequence);
        Assert.Equal(TimeSpan.Zero, snapshot.CheckedAt.Offset);

        QuotaDeliveryHealthSnapshot completedReplay = await ReadAsync(
            scenario.GroupId,
            [scenario.ExpectedSourceEventSequences[4]],
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, completedReplay.OriginalCount);
        Assert.Equal(0, completedReplay.MissingOriginalCount);
        Assert.Equal(0, completedReplay.DuplicateOriginalCount);
        Assert.Equal(0, completedReplay.PendingLineageCount);
        Assert.Equal(0, completedReplay.ProcessingLineageCount);
        Assert.Equal(0, completedReplay.DeadLineageCount);
        Assert.Equal(0, completedReplay.OldestUnresolvedAgeSeconds);
        Assert.Null(completedReplay.BlockingSourceEventSequence);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderReportsDuplicateOriginalWithoutParsingPayload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DuplicateScenario scenario = await SeedDuplicateOriginalAsync(
            cancellationToken).ConfigureAwait(true);

        try
        {
            QuotaDeliveryHealthSnapshot snapshot = await ReadAsync(
                scenario.GroupId,
                [scenario.SourceEventSequence],
                cancellationToken).ConfigureAwait(true);

            Assert.Equal(2, snapshot.OriginalCount);
            Assert.Equal(0, snapshot.MissingOriginalCount);
            Assert.Equal(1, snapshot.DuplicateOriginalCount);
            Assert.Equal(0, snapshot.PendingLineageCount);
            Assert.Equal(0, snapshot.ProcessingLineageCount);
            Assert.Equal(0, snapshot.DeadLineageCount);
            Assert.Equal(0, snapshot.OldestUnresolvedAgeSeconds);
            Assert.Equal(
                scenario.SourceEventSequence,
                snapshot.BlockingSourceEventSequence);
        }
        finally
        {
            await RestoreOriginalLineageConstraintAsync(
                scenario.GroupId,
                cancellationToken).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReaderDetectsCheckpointCoveredInboxMissingAndConflict()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        InboxScenario scenario = await SeedInboxScenarioAsync(cancellationToken)
            .ConfigureAwait(true);

        QuotaDeliveryHealthSnapshot snapshot = await ReadAsync(
            scenario.GroupId,
            scenario.ExpectedSourceEventSequences,
            cancellationToken,
            scenario.CheckpointSourceEventSequence).ConfigureAwait(true);

        Assert.Equal(4, snapshot.ExpectedInboxReceiptCount);
        Assert.Equal(1, snapshot.MissingInboxReceiptCount);
        Assert.Equal(1, snapshot.ConflictingInboxReceiptCount);
        Assert.Equal(
            scenario.MissingSourceEventSequence,
            snapshot.BlockingSourceEventSequence);
        Assert.InRange(snapshot.OldestUnresolvedAgeSeconds, 420, 1200);

        QuotaDeliveryHealthSnapshot beforeCheckpoint = await ReadAsync(
            scenario.GroupId,
            scenario.ExpectedSourceEventSequences,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(0, beforeCheckpoint.ExpectedInboxReceiptCount);
        Assert.Equal(0, beforeCheckpoint.MissingInboxReceiptCount);
        Assert.Equal(0, beforeCheckpoint.ConflictingInboxReceiptCount);
        Assert.Null(beforeCheckpoint.BlockingSourceEventSequence);
    }

    private async ValueTask<InboxScenario> SeedInboxScenarioAsync(
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long first = await NextSourceSequenceAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        EntityId exact = await InsertOutboxAsync(
            connection, transaction, groupId, first, "published",
            now.AddMinutes(-10), null, "group", cancellationToken).ConfigureAwait(false);
        EntityId replayed = await InsertOutboxAsync(
            connection, transaction, groupId, first + 1, "dead",
            now.AddMinutes(-9), null, "group", cancellationToken).ConfigureAwait(false);
        EntityId replay = await InsertOutboxAsync(
            connection, transaction, groupId, first + 1, "published",
            now.AddMinutes(-9), replayed, "group", cancellationToken).ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, first + 2, "published",
            now.AddMinutes(-8), null, "group", cancellationToken).ConfigureAwait(false);
        EntityId conflicting = await InsertOutboxAsync(
            connection, transaction, groupId, first + 3, "published",
            now.AddMinutes(-7), null, "group", cancellationToken).ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, first + 4, "published",
            now.AddMinutes(-6), null, "group", cancellationToken).ConfigureAwait(false);
        await InsertInboxAsync(
            connection, transaction, exact, schemaVersion: 1, cancellationToken)
            .ConfigureAwait(false);
        await InsertInboxAsync(
            connection, transaction, replay, schemaVersion: 1, cancellationToken)
            .ConfigureAwait(false);
        await InsertInboxAsync(
            connection, transaction, conflicting, schemaVersion: 2, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(groupId, first);
    }

    private static async ValueTask InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId messageId,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            """
            INSERT INTO public.inbox_messages (
                consumer_name, message_id, topic, event_sequence,
                schema_version, payload_hash)
            SELECT 'usage-hourly-v1', id, topic, event_sequence, $2,
                   decode(repeat('a5', 32), 'hex')
            FROM public.outbox_messages
            WHERE id = $1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, messageId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, schemaVersion);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<DuplicateScenario> SeedDuplicateOriginalAsync(
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long sourceEventSequence = await NextSourceSequenceAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await DropOriginalLineageConstraintAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, sourceEventSequence, "published",
            now.AddMinutes(-2), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, sourceEventSequence, "published",
            now.AddMinutes(-1), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(groupId, sourceEventSequence);
    }

    private async ValueTask<QuotaDeliveryHealthSnapshot> ReadAsync(
        EntityId groupId,
        IReadOnlyList<long> expectedSourceEventSequences,
        CancellationToken cancellationToken,
        long checkpointSourceEventSequence = 0)
    {
        PostgresQuotaDeliveryHealthReader reader = new();
        IUnitOfWorkFactory factory = fixture.WorkerServices
            .GetRequiredService<IUnitOfWorkFactory>();
        IUnitOfWork unitOfWork = await factory.BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        QuotaDeliveryHealthSnapshot snapshot = await reader.ReadAsync(
            groupId,
            expectedSourceEventSequences,
            checkpointSourceEventSequence,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private async ValueTask<SeededScenario> SeedAsync(
        CancellationToken cancellationToken)
    {
        EntityId groupId = EntityId.New();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long firstSourceSequence = await NextSourceSequenceAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await InsertCompletedLineagesAsync(
            connection,
            transaction,
            groupId,
            firstSourceSequence,
            now,
            cancellationToken).ConfigureAwait(false);
        await InsertUnresolvedLineagesAsync(
            connection,
            transaction,
            groupId,
            firstSourceSequence,
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededScenario(groupId, firstSourceSequence);
    }

    private static async ValueTask InsertCompletedLineagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId groupId,
        long firstSourceSequence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence, "published",
            now.AddMinutes(-10), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        EntityId replayedOriginal = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 8, "dead",
            now.AddMinutes(-20), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 8, "published",
            now.AddMinutes(-20), replayedOriginal, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 1, "published",
            now.AddMinutes(-1), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask InsertUnresolvedLineagesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId groupId,
        long firstSourceSequence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EntityId pendingReplayOriginal = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 2, "dead",
            now.AddMinutes(-5), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 2, "pending",
            now.AddMinutes(-5), pendingReplayOriginal, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 4, "processing",
            now.AddMinutes(-4), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
        _ = await InsertOutboxAsync(
            connection, transaction, groupId, firstSourceSequence + 6, "dead",
            now.AddMinutes(-3), replayOf: null, "group", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask<long> NextSourceSequenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            "SELECT coalesce(max(source_event_sequence), 0) + 10 "
                + "FROM public.outbox_messages;",
            connection,
            transaction);
        object? value = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<long>(value);
    }

    private static async ValueTask DropOriginalLineageConstraintAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = new(
            "DROP INDEX public.uq_outbox_messages_topic_source_sequence;",
            connection,
            transaction);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RestoreOriginalLineageConstraintAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlConnection connection = await fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlCommand delete = new(
            "DELETE FROM public.outbox_messages WHERE aggregate_id = $1;",
            connection,
            transaction);
        delete.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlCommand create = new(
            """
            CREATE UNIQUE INDEX uq_outbox_messages_topic_source_sequence
                ON public.outbox_messages(topic, source_event_sequence)
                WHERE source_event_sequence IS NOT NULL AND replay_of IS NULL;
            """,
            connection,
            transaction);
        _ = await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<EntityId> InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EntityId groupId,
        long sourceEventSequence,
        string status,
        DateTimeOffset occurredAt,
        EntityId? replayOf,
        string aggregateType,
        CancellationToken cancellationToken)
    {
        EntityId messageId = EntityId.New();
        using NpgsqlCommand command = new(InsertOutboxSql, connection, transaction);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, messageId.Value);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Text,
            $"m3e5-delivery-health-{messageId.Value:N}");
        command.Parameters.AddWithValue(NpgsqlDbType.Text, aggregateType);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, sourceEventSequence);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, EntityId.New().Value);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Jsonb,
            "{\"ignored_secret\":\"must-not-be-parsed-or-returned\"}");
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, occurredAt);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, status);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, EntityId.New().Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = replayOf is { } value ? value.Value : DBNull.Value,
        });
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return messageId;
    }

    private const string InsertOutboxSql = """
        INSERT INTO public.outbox_messages (
            id, deduplication_key, topic, schema_version, aggregate_type,
            aggregate_id, aggregate_version, event_type, source_event_sequence,
            correlation_id, causation_id, payload, occurred_at, status,
            next_attempt_at, publish_attempts, locked_by, lock_generation,
            locked_until, published_at, dead_at, replay_of, last_error)
        VALUES (
            $1, $2, 'poolai.quota.v1', 1, $3,
            $4, NULL, 'settled', $5,
            $6, NULL, $7, $8, $9,
            CASE WHEN $9 IN ('pending', 'processing')
                THEN clock_timestamp() ELSE NULL END,
            CASE WHEN $9 = 'pending' THEN 0 ELSE 1 END,
            CASE WHEN $9 = 'processing' THEN $10 ELSE NULL END,
            CASE WHEN $9 = 'pending' THEN 0 ELSE 1 END,
            CASE WHEN $9 = 'processing'
                THEN clock_timestamp() + interval '10 minutes' ELSE NULL END,
            CASE WHEN $9 = 'published' THEN clock_timestamp() ELSE NULL END,
            CASE WHEN $9 = 'dead' THEN clock_timestamp() ELSE NULL END,
            $11,
            CASE WHEN $9 = 'dead' THEN 'test_poison' ELSE NULL END);
        """;

    private sealed record SeededScenario(
        EntityId GroupId,
        long FirstSourceSequence)
    {
        internal IReadOnlyList<long> ExpectedSourceEventSequences =>
            [
                FirstSourceSequence,
                FirstSourceSequence + 2,
                FirstSourceSequence + 4,
                FirstSourceSequence + 6,
                FirstSourceSequence + 8,
                FirstSourceSequence + 10,
            ];
    }

    private sealed record DuplicateScenario(
        EntityId GroupId,
        long SourceEventSequence);

    private sealed record InboxScenario(EntityId GroupId, long FirstSourceEventSequence)
    {
        internal IReadOnlyList<long> ExpectedSourceEventSequences =>
            Enumerable.Range(0, 5)
                .Select(offset => FirstSourceEventSequence + offset)
                .ToArray();

        internal long CheckpointSourceEventSequence => FirstSourceEventSequence + 3;

        internal long MissingSourceEventSequence => FirstSourceEventSequence + 2;
    }
}
