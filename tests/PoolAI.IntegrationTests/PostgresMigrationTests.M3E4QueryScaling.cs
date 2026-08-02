using Npgsql;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresMigrationTests
{
    private static readonly string[] M3E4OutboxQueryScalingIndexNames =
    [
        "ix_outbox_messages_backlog_metrics",
        "ix_outbox_messages_dead_metrics",
        "ix_outbox_messages_published_lineage",
        "ix_outbox_messages_replay_metrics",
        "ix_outbox_messages_unresolved_lineage",
    ];

    private static async Task AssertM3E4OutboxQueryScalingIndexesAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        Dictionary<string, M3E4OutboxIndexDefinition> indexes =
            await ReadM3E4OutboxQueryScalingIndexesAsync(
                connectionString,
                cancellationToken).ConfigureAwait(false);

        Assert.Equal(M3E4OutboxQueryScalingIndexNames.Length, indexes.Count);
        Assert.All(indexes.Values, AssertM3E4OutboxIndexIsUsable);
        AssertM3E4LineageIndexes(indexes);
        AssertM3E4ObservabilityIndexes(indexes);
    }

    private static async Task<Dictionary<string, M3E4OutboxIndexDefinition>>
        ReadM3E4OutboxQueryScalingIndexesAsync(
            string connectionString,
            CancellationToken cancellationToken)
    {
        Dictionary<string, M3E4OutboxIndexDefinition> indexes =
            new(StringComparer.Ordinal);
        using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT index_relation.relname,
                   index.indisunique,
                   index.indisvalid,
                   index.indisready,
                   pg_catalog.pg_get_indexdef(index_relation.oid),
                   pg_catalog.pg_get_expr(index.indpred, index.indrelid)
            FROM pg_catalog.pg_index AS index
            JOIN pg_catalog.pg_class AS index_relation
              ON index_relation.oid = index.indexrelid
            JOIN pg_catalog.pg_class AS table_relation
              ON table_relation.oid = index.indrelid
            JOIN pg_catalog.pg_namespace AS namespace
              ON namespace.oid = table_relation.relnamespace
            WHERE namespace.nspname = 'public'
              AND table_relation.relname = 'outbox_messages'
              AND index_relation.relname = ANY($1)
            ORDER BY index_relation.relname;
            """;
        command.Parameters.AddWithValue(M3E4OutboxQueryScalingIndexNames);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexes.Add(
                reader.GetString(0),
                new M3E4OutboxIndexDefinition(
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    NormalizeM3E4IndexSql(reader.GetString(4)),
                    NormalizeM3E4IndexSql(reader.GetString(5))));
        }

        return indexes;
    }

    private static void AssertM3E4OutboxIndexIsUsable(
        M3E4OutboxIndexDefinition index)
    {
        Assert.False(index.IsUnique);
        Assert.True(index.IsValid);
        Assert.True(index.IsReady);
    }

    private static void AssertM3E4LineageIndexes(
        IReadOnlyDictionary<string, M3E4OutboxIndexDefinition> indexes)
    {
        M3E4OutboxIndexDefinition unresolved =
            indexes["ix_outbox_messages_unresolved_lineage"];
        Assert.Contains(
            "(topic, aggregate_id, source_event_sequence, event_sequence)",
            unresolved.Definition,
            StringComparison.Ordinal);
        Assert.Contains("status <> 'published'", unresolved.Predicate, StringComparison.Ordinal);

        M3E4OutboxIndexDefinition published =
            indexes["ix_outbox_messages_published_lineage"];
        Assert.Contains(
            "(topic, aggregate_id, coalesce(source_event_sequence",
            published.Definition,
            StringComparison.Ordinal);
        Assert.Contains("status = 'published'", published.Predicate, StringComparison.Ordinal);
    }

    private static void AssertM3E4ObservabilityIndexes(
        IReadOnlyDictionary<string, M3E4OutboxIndexDefinition> indexes)
    {
        M3E4OutboxIndexDefinition backlog =
            indexes["ix_outbox_messages_backlog_metrics"];
        Assert.Contains("(occurred_at, event_sequence)", backlog.Definition, StringComparison.Ordinal);
        Assert.Contains("status", backlog.Predicate, StringComparison.Ordinal);
        Assert.Contains("'pending'", backlog.Predicate, StringComparison.Ordinal);
        Assert.Contains("'processing'", backlog.Predicate, StringComparison.Ordinal);

        M3E4OutboxIndexDefinition dead = indexes["ix_outbox_messages_dead_metrics"];
        Assert.Contains("(event_sequence)", dead.Definition, StringComparison.Ordinal);
        Assert.Contains("status = 'dead'", dead.Predicate, StringComparison.Ordinal);

        M3E4OutboxIndexDefinition replay = indexes["ix_outbox_messages_replay_metrics"];
        Assert.Contains("(event_sequence)", replay.Definition, StringComparison.Ordinal);
        Assert.Contains("replay_of IS NOT NULL", replay.Predicate, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("last_error", dead.Definition, StringComparison.Ordinal);
        Assert.DoesNotContain("event_type", dead.Definition, StringComparison.Ordinal);
        Assert.DoesNotContain("event_type", replay.Definition, StringComparison.Ordinal);
    }

    private static string NormalizeM3E4IndexSql(string value) =>
        string.Join(
                " ",
                value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private sealed record M3E4OutboxIndexDefinition(
        bool IsUnique,
        bool IsValid,
        bool IsReady,
        string Definition,
        string Predicate);
}
