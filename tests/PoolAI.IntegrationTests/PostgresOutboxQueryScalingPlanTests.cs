using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresOutboxQueryScalingPlanTests
{
    private const int PublishedHistoryCount = 50_000;
    private const long SourceSequenceBase = 8_000_000_000_000_000_000;
    private static readonly string[] QuotaTopics = ["poolai.quota.v1"];
    private readonly PostgresRuntimeFixture _fixture;

    public PostgresOutboxQueryScalingPlanTests(PostgresRuntimeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ClaimAndTelemetryPlansExcludePublishedNonReplayHistory()
    {
        // Governing contracts: ADR 0012 and database acceptance item 32.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using NpgsqlConnection connection = await _fixture.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        Guid groupId = Guid.CreateVersion7();
        string prefix = $"plan:{Guid.CreateVersion7():N}";

        await SeedPublishedHistoryAsync(
            connection,
            transaction,
            groupId,
            prefix,
            cancellationToken).ConfigureAwait(true);
        await AnalyzeOutboxAsync(connection, transaction, cancellationToken).ConfigureAwait(true);
        OutboxPlan idle = await ExplainClaimAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(true);

        long exactQuotaSourceSequence = await SeedExactReplayScenarioAsync(
            connection,
            transaction,
            groupId,
            prefix,
            cancellationToken).ConfigureAwait(true);
        await AnalyzeOutboxAsync(connection, transaction, cancellationToken).ConfigureAwait(true);
        OutboxPlan telemetry = await ExplainTelemetryAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(true);
        OutboxPlan replay = await ExplainClaimAsync(
            connection,
            transaction,
            cancellationToken).ConfigureAwait(true);
        OutboxPlan quotaDeliveryHealth = await ExplainQuotaDeliveryHealthAsync(
            connection,
            transaction,
            groupId,
            exactQuotaSourceSequence,
            cancellationToken).ConfigureAwait(true);

        AssertBounded(idle, "ix_outbox_messages_unresolved_lineage");
        AssertBounded(
            replay,
            "ix_outbox_messages_unresolved_lineage",
            "ix_outbox_messages_published_lineage");
        AssertBounded(
            telemetry,
            "ix_outbox_messages_backlog_metrics",
            "ix_outbox_messages_dead_metrics",
            "ix_outbox_messages_replay_metrics");
        AssertBounded(
            quotaDeliveryHealth,
            "ix_outbox_messages_unresolved_lineage",
            "ix_outbox_messages_published_lineage");
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
    }

    private static async Task SeedPublishedHistoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        string prefix,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version, aggregate_type,
                aggregate_id, event_type, source_event_sequence, correlation_id,
                payload, occurred_at, status, next_attempt_at, publish_attempts,
                lock_generation, published_at
            )
            SELECT gen_random_uuid(),
                   $1 || ':' || source::text,
                   'poolai.quota.v1', 1, 'group', $2, 'reserved',
                   $3 + source, $2, '{}'::jsonb,
                   timestamptz '2026-08-02 00:00:00+00',
                   'published', NULL, 1, 1, clock_timestamp()
            FROM generate_series(1, $4) AS source;
            """;
        command.Parameters.AddWithValue(prefix);
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(SourceSequenceBase);
        command.Parameters.AddWithValue(PublishedHistoryCount);
        Assert.Equal(
            PublishedHistoryCount,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<long> SeedExactReplayScenarioAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        string prefix,
        CancellationToken cancellationToken)
    {
        Guid sourceId = Guid.CreateVersion7();
        DateTimeOffset occurredAt = new(2026, 8, 2, 1, 0, 0, TimeSpan.Zero);
        long sourceSequence = SourceSequenceBase + PublishedHistoryCount + 1;
        await InsertDeadSourceAsync(
            connection, transaction, sourceId, groupId, prefix,
            sourceSequence, occurredAt, cancellationToken).ConfigureAwait(false);
        await InsertReplayAsync(
            connection, transaction, sourceId, groupId, prefix,
            sourceSequence, occurredAt, published: true, cancellationToken).ConfigureAwait(false);
        await InsertReplayAsync(
            connection, transaction, sourceId, groupId, prefix,
            sourceSequence, occurredAt, published: false, cancellationToken).ConfigureAwait(false);
        return sourceSequence;
    }

    private static async Task InsertDeadSourceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        Guid groupId,
        string prefix,
        long sourceSequence,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = CreateEnvelopeInsertCommand(
            connection, transaction, sourceId, groupId, prefix,
            sourceSequence, occurredAt);
        command.CommandText += """
            , 'dead', NULL, 1, 1, clock_timestamp(), 'contract_mismatch', NULL, NULL
            );
            """;
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task InsertReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        Guid groupId,
        string prefix,
        long sourceSequence,
        DateTimeOffset occurredAt,
        bool published,
        CancellationToken cancellationToken)
    {
        Guid replayId = Guid.CreateVersion7();
        using NpgsqlCommand command = CreateEnvelopeInsertCommand(
            connection, transaction, replayId, groupId,
            $"{prefix}:replay:{replayId:N}", sourceSequence, occurredAt);
        command.CommandText += published
            ? """
                , 'published', NULL, 1, 1, NULL, NULL, clock_timestamp(), $6
                );
                """
            : """
                , 'pending', clock_timestamp(), 0, 0, NULL, NULL, NULL, $6
                );
                """;
        command.Parameters.AddWithValue(sourceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static NpgsqlCommand CreateEnvelopeInsertCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        Guid groupId,
        string deduplicationKey,
        long sourceSequence,
        DateTimeOffset occurredAt)
    {
        NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.outbox_messages (
                id, deduplication_key, topic, schema_version, aggregate_type,
                aggregate_id, event_type, source_event_sequence, correlation_id,
                payload, occurred_at, status, next_attempt_at, publish_attempts,
                lock_generation, dead_at, last_error, published_at, replay_of
            ) VALUES (
                $1, $2, 'poolai.quota.v1', 1, 'group', $3, 'reserved',
                $4, $3, '{}'::jsonb, $5
            """;
        command.Parameters.AddWithValue(messageId);
        command.Parameters.AddWithValue(deduplicationKey);
        command.Parameters.AddWithValue(groupId);
        command.Parameters.AddWithValue(sourceSequence);
        command.Parameters.AddWithValue(occurredAt);
        return command;
    }

    private static async Task AnalyzeOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET LOCAL default_statistics_target = 10000;
            ANALYZE public.outbox_messages;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutboxPlan> ExplainClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + ReadSqlConstant(typeof(PostgresOutboxDeliveryStore), "ClaimRoutedSql");
        command.Parameters.AddWithValue(QuotaTopics);
        command.Parameters.AddWithValue(Guid.CreateVersion7());
        command.Parameters.AddWithValue(1);
        command.Parameters.AddWithValue(TimeSpan.FromMinutes(5));
        return await ReadPlanAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutboxPlan> ExplainTelemetryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + ReadSqlConstant(typeof(PostgresOutboxObservabilityStore), "ObservabilitySql");
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.EventTypes.ToArray());
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.Topics.ToArray());
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.Reasons.ToArray());
        return await ReadPlanAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutboxPlan> ExplainQuotaDeliveryHealthAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid groupId,
        long sourceEventSequence,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "
            + ReadSqlConstant(typeof(PostgresQuotaDeliveryHealthReader), "ReadSql");
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            new[] { sourceEventSequence });
        command.Parameters.AddWithValue(NpgsqlDbType.Bigint, sourceEventSequence);
        return await ReadPlanAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutboxPlan> ReadPlanAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        object? raw = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        string json = Convert.ToString(raw, CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("PostgreSQL returned no JSON plan.");
        using JsonDocument document = JsonDocument.Parse(json);
        List<OutboxPlanNode> nodes = [];
        CollectNodes(document.RootElement[0].GetProperty("Plan"), nodes);
        return new OutboxPlan(nodes);
    }

    private static void CollectNodes(JsonElement plan, ICollection<OutboxPlanNode> nodes)
    {
        nodes.Add(new OutboxPlanNode(
            ReadString(plan, "Node Type"),
            ReadString(plan, "Relation Name"),
            ReadString(plan, "Index Name"),
            ReadRows(plan, "Actual Rows"),
            ReadRows(plan, "Rows Removed by Filter")
                + ReadRows(plan, "Rows Removed by Index Recheck"),
            ReadRows(plan, "Actual Loops")));
        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                CollectNodes(child, nodes);
            }
        }
    }

    private static void AssertBounded(OutboxPlan plan, params string[] expectedIndexes)
    {
        foreach (string expectedIndex in expectedIndexes)
        {
            Assert.True(
                plan.Nodes.Any(node => string.Equals(
                    node.IndexName,
                    expectedIndex,
                    StringComparison.Ordinal)),
                $"Expected index {expectedIndex}; actual indexes: "
                + string.Join(
                    ", ",
                    plan.Nodes
                        .Select(node => node.IndexName)
                        .Where(index => !string.IsNullOrWhiteSpace(index))
                        .Distinct(StringComparer.Ordinal)));
        }

        long forbiddenWork = PublishedHistoryCount / 10;
        Assert.DoesNotContain(plan.Nodes, node =>
            node.TouchesOutbox && node.RowsExamined >= forbiddenWork);
    }

    private static string ReadSqlConstant(Type owner, string name) =>
        owner.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)?
            .GetRawConstantValue() as string
        ?? throw new InvalidOperationException($"Could not read {owner.Name}.{name}.");

    private static string? ReadString(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property) ? property.GetString() : null;

    private static long ReadRows(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement property)
            ? (long)Math.Ceiling(property.GetDouble())
            : 0;

    private sealed record OutboxPlan(IReadOnlyList<OutboxPlanNode> Nodes);

    private sealed record OutboxPlanNode(
        string? NodeType,
        string? RelationName,
        string? IndexName,
        long ActualRows,
        long RemovedRows,
        long ActualLoops)
    {
        internal bool TouchesOutbox =>
            string.Equals(RelationName, "outbox_messages", StringComparison.Ordinal)
            || IndexName?.StartsWith("ix_outbox_messages_", StringComparison.Ordinal) == true
            || IndexName?.StartsWith("uq_outbox_messages_", StringComparison.Ordinal) == true;

        internal long RowsExamined => (ActualRows + RemovedRows) * ActualLoops;
    }
}
