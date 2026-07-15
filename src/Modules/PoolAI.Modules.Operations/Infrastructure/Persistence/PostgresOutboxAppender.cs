using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresOutboxAppender : IOutboxAppender
{
    private const string InsertSql = """
        INSERT INTO public.outbox_messages (
            id, deduplication_key, topic, schema_version, aggregate_type,
            aggregate_id, aggregate_version, event_type, source_event_sequence,
            correlation_id, causation_id, payload, occurred_at
        ) VALUES (
            $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12::jsonb, $13
        );
        """;

    public async ValueTask AppendAsync(
        IntegrationEvent integrationEvent,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        Validate(integrationEvent);

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(InsertSql);
        command.Parameters.AddWithValue(integrationEvent.MessageId.Value);
        command.Parameters.AddWithValue(integrationEvent.DeduplicationKey);
        command.Parameters.AddWithValue(integrationEvent.Topic);
        command.Parameters.AddWithValue(integrationEvent.SchemaVersion);
        command.Parameters.AddWithValue(integrationEvent.AggregateType);
        command.Parameters.AddWithValue(integrationEvent.AggregateId.Value);
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Bigint,
            integrationEvent.AggregateVersion);
        command.Parameters.AddWithValue(integrationEvent.EventType);
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Bigint,
            integrationEvent.SourceEventSequence);
        command.Parameters.AddWithValue(integrationEvent.CorrelationId.Value);
        PostgresPersistenceGuard.AddNullable(
            command.Parameters,
            NpgsqlDbType.Uuid,
            integrationEvent.CausationId?.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb,
            Value = integrationEvent.Payload.GetRawText(),
        });
        command.Parameters.AddWithValue(integrationEvent.OccurredAt.ToUniversalTime());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(IntegrationEvent integrationEvent)
    {
        PostgresPersistenceGuard.NotBlank(
            integrationEvent.DeduplicationKey,
            nameof(integrationEvent.DeduplicationKey));
        PostgresPersistenceGuard.NotBlank(integrationEvent.Topic, nameof(integrationEvent.Topic));
        PostgresPersistenceGuard.NotBlank(
            integrationEvent.AggregateType,
            nameof(integrationEvent.AggregateType));
        PostgresPersistenceGuard.NotBlank(
            integrationEvent.EventType,
            nameof(integrationEvent.EventType));
        PostgresPersistenceGuard.JsonObject(integrationEvent.Payload, nameof(integrationEvent.Payload));
        if (integrationEvent.SchemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(integrationEvent), "Schema version must be positive.");
        }

        if (integrationEvent.AggregateVersion is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(integrationEvent), "Aggregate version must be positive.");
        }

        if (integrationEvent.SourceEventSequence is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(integrationEvent), "Source event sequence must be positive.");
        }
    }
}
