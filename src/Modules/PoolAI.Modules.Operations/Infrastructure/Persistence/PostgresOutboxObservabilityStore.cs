using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresOutboxObservabilityStore : IOutboxObservabilityStore
{
    private const string ObservabilitySql = """
        WITH backlog AS MATERIALIZED (
            SELECT CASE WHEN event_type = ANY($1) THEN event_type ELSE 'other' END
                       AS event_type,
                   count(*) AS message_count,
                   greatest(
                       coalesce(
                           extract(epoch FROM clock_timestamp() - min(occurred_at)),
                           0),
                       0)::double precision AS oldest_age_seconds
            FROM public.outbox_messages
            WHERE status IN ('pending', 'processing')
            GROUP BY 1
        ),
        terminal AS MATERIALIZED (
            SELECT 'dead'::text AS metric_kind,
                   CASE WHEN topic = ANY($2) THEN topic ELSE 'other' END AS topic,
                   CASE WHEN event_type = ANY($1) THEN event_type ELSE 'other' END
                       AS event_type,
                   CASE
                       WHEN coalesce(last_error, 'unknown') = ANY($3)
                           THEN coalesce(last_error, 'unknown')
                       ELSE 'unknown'
                   END AS reason,
                   count(*) AS message_count
            FROM public.outbox_messages
            WHERE status = 'dead'
            GROUP BY 1, 2, 3, 4

            UNION ALL

            SELECT 'replay'::text,
                   CASE WHEN topic = ANY($2) THEN topic ELSE 'other' END,
                   CASE WHEN event_type = ANY($1) THEN event_type ELSE 'other' END,
                   'created'::text,
                   count(*)
            FROM public.outbox_messages
            WHERE replay_of IS NOT NULL
            GROUP BY 1, 2, 3, 4
        )
        SELECT 'backlog'::text AS metric_kind,
               NULL::text AS topic,
               backlog.event_type,
               NULL::text AS reason,
               backlog.message_count,
               backlog.oldest_age_seconds
        FROM backlog

        UNION ALL

        SELECT terminal.metric_kind,
               terminal.topic,
               terminal.event_type,
               terminal.reason,
               terminal.message_count,
               NULL::double precision
        FROM terminal
        ORDER BY 1, 2 NULLS FIRST, 3, 4 NULLS FIRST;
        """;

    public async ValueTask<OutboxObservabilitySnapshot> ReadAsync(
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ObservabilitySql);
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.EventTypes.ToArray());
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.Topics.ToArray());
        command.Parameters.AddWithValue(OutboxTelemetryClassifier.Reasons.ToArray());
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        List<OutboxBacklogMetric> backlog = [];
        List<OutboxTerminalMetric> dead = [];
        List<OutboxTerminalMetric> replays = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string metricKind = reader.GetString(0);
            if (string.Equals(metricKind, "backlog", StringComparison.Ordinal))
            {
                backlog.Add(new OutboxBacklogMetric(
                    reader.GetString(2),
                    reader.GetInt64(4),
                    reader.GetDouble(5)));
                continue;
            }

            OutboxTerminalMetric metric = ReadTerminalMetric(reader);
            if (string.Equals(metricKind, "dead", StringComparison.Ordinal))
            {
                dead.Add(metric);
            }
            else if (string.Equals(metricKind, "replay", StringComparison.Ordinal))
            {
                replays.Add(metric);
            }
            else
            {
                throw new InvalidOperationException(
                    "The Outbox observability query returned an unknown metric kind.");
            }
        }

        return new OutboxObservabilitySnapshot(backlog, dead, replays);
    }

    private static OutboxTerminalMetric ReadTerminalMetric(NpgsqlDataReader reader) =>
        new(
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4));
}
