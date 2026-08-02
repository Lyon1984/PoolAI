using System.Globalization;
using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresOutboxDeliveryStore : IOutboxDeliveryStore
{
    private const string QuotaTopic = "poolai.quota.v1";
    private const string ClaimRoutedSql = """
        WITH unresolved_lineages AS MATERIALIZED (
            SELECT message.topic,
                   CASE
                       WHEN message.topic = 'poolai.quota.v1' THEN 'group'
                       ELSE message.aggregate_type
                   END AS partition_type,
                   message.aggregate_id,
                   message.source_event_sequence,
                   min(message.event_sequence) AS first_event_sequence
            FROM public.outbox_messages AS message
            WHERE message.topic = ANY($1)
              AND message.status <> 'published'
            GROUP BY message.topic,
                     CASE
                         WHEN message.topic = 'poolai.quota.v1' THEN 'group'
                         ELSE message.aggregate_type
                     END,
                     message.aggregate_id,
                     message.source_event_sequence
        ),
        lineage_state AS MATERIALIZED (
            SELECT lineage.topic,
                   lineage.partition_type,
                   lineage.aggregate_id,
                   lineage.source_event_sequence,
                   lineage.first_event_sequence,
                   EXISTS (
                       SELECT 1
                       FROM public.outbox_messages AS published
                       WHERE published.status = 'published'
                         AND published.topic = lineage.topic
                         AND CASE
                             WHEN published.topic = 'poolai.quota.v1' THEN 'group'
                             ELSE published.aggregate_type
                         END = lineage.partition_type
                         AND published.aggregate_id = lineage.aggregate_id
                         AND coalesce(published.source_event_sequence, 0) =
                             coalesce(lineage.source_event_sequence, 0)
                   ) AS is_complete
            FROM unresolved_lineages AS lineage
        ),
        earliest_incomplete AS MATERIALIZED (
            SELECT DISTINCT ON (
                       lineage.topic,
                       lineage.partition_type,
                       lineage.aggregate_id)
                   lineage.topic,
                   lineage.partition_type,
                   lineage.aggregate_id,
                   lineage.source_event_sequence,
                   lineage.first_event_sequence
            FROM lineage_state AS lineage
            WHERE NOT lineage.is_complete
            ORDER BY lineage.topic,
                     lineage.partition_type,
                     lineage.aggregate_id,
                     CASE WHEN lineage.source_event_sequence IS NULL THEN 0 ELSE 1 END,
                     lineage.source_event_sequence,
                     lineage.first_event_sequence
        ),
        exact_completed_replays AS MATERIALIZED (
            SELECT replay.id
            FROM public.outbox_messages AS replay
            INNER JOIN public.outbox_messages AS source
                ON source.id = replay.replay_of
               AND source.status = 'dead'
            WHERE replay.topic = ANY($1)
              AND replay.status IN ('pending', 'processing')
              AND replay.topic = source.topic
              AND replay.schema_version = source.schema_version
              AND replay.aggregate_type = source.aggregate_type
              AND replay.aggregate_id = source.aggregate_id
              AND replay.aggregate_version IS NOT DISTINCT FROM source.aggregate_version
              AND replay.event_type = source.event_type
              AND replay.source_event_sequence IS NOT DISTINCT FROM
                  source.source_event_sequence
              AND replay.correlation_id = source.correlation_id
              AND replay.causation_id IS NOT DISTINCT FROM source.causation_id
              AND replay.payload = source.payload
              AND replay.occurred_at = source.occurred_at
              AND EXISTS (
                    SELECT 1
                    FROM public.outbox_messages AS published
                    WHERE published.status = 'published'
                      AND published.topic = source.topic
                      AND published.schema_version = source.schema_version
                      AND published.aggregate_type = source.aggregate_type
                      AND published.aggregate_id = source.aggregate_id
                      AND published.aggregate_version IS NOT DISTINCT FROM
                          source.aggregate_version
                      AND published.event_type = source.event_type
                      AND coalesce(published.source_event_sequence, 0) =
                          coalesce(source.source_event_sequence, 0)
                      AND published.correlation_id = source.correlation_id
                      AND published.causation_id IS NOT DISTINCT FROM
                          source.causation_id
                      AND published.payload = source.payload
                      AND published.occurred_at = source.occurred_at
              )
        ),
        candidates AS MATERIALIZED (
            SELECT message.id,
                   lineage.is_complete
                       AND exact_replay.id IS NOT NULL AS lineage_already_published
            FROM public.outbox_messages AS message
            INNER JOIN lineage_state AS lineage
                ON lineage.topic = message.topic
               AND lineage.partition_type = CASE
                   WHEN message.topic = 'poolai.quota.v1' THEN 'group'
                   ELSE message.aggregate_type
               END
               AND lineage.aggregate_id = message.aggregate_id
               AND lineage.source_event_sequence IS NOT DISTINCT FROM
                   message.source_event_sequence
            LEFT JOIN earliest_incomplete AS earliest
                ON earliest.topic = message.topic
               AND earliest.partition_type = lineage.partition_type
               AND earliest.aggregate_id = message.aggregate_id
            LEFT JOIN exact_completed_replays AS exact_replay
                ON exact_replay.id = message.id
            WHERE message.topic = ANY($1)
              AND (
                    (message.status = 'pending'
                     AND message.next_attempt_at <= clock_timestamp())
                 OR (message.status = 'processing'
                     AND message.locked_until <= clock_timestamp()
                     AND message.locked_by IS DISTINCT FROM $2)
              )
              AND (
                    lineage.is_complete
                 OR (
                        earliest.source_event_sequence IS NOT DISTINCT FROM
                            message.source_event_sequence
                    AND earliest.first_event_sequence = lineage.first_event_sequence
                 )
              )
            ORDER BY
                CASE WHEN lineage.is_complete THEN 1 ELSE 0 END,
                CASE WHEN message.status = 'pending'
                    THEN message.next_attempt_at ELSE message.locked_until END,
                CASE WHEN message.source_event_sequence IS NULL THEN 0 ELSE 1 END,
                message.source_event_sequence,
                message.event_sequence
            FOR UPDATE OF message SKIP LOCKED
            LIMIT $3
        )
        UPDATE public.outbox_messages AS message
        SET status = 'processing',
            locked_by = $2,
            lock_generation = message.lock_generation + 1,
            publish_attempts = message.publish_attempts + 1,
            locked_until = clock_timestamp() + $4
        FROM candidates
        WHERE message.id = candidates.id
        RETURNING message.id,
                  message.event_sequence,
                  message.deduplication_key,
                  message.topic,
                  message.schema_version,
                  message.aggregate_type,
                  message.aggregate_id,
                  message.aggregate_version,
                  message.event_type,
                  message.source_event_sequence,
                  message.correlation_id,
                  message.causation_id,
                  message.payload::text,
                  message.occurred_at,
                  message.replay_of,
                  message.lock_generation,
                  message.publish_attempts,
                  candidates.lineage_already_published;
        """;

    private const string ClaimSql = """
        WITH candidates AS MATERIALIZED (
            SELECT id
            FROM public.outbox_messages
            WHERE (status = 'pending' AND next_attempt_at <= clock_timestamp())
               OR (status = 'processing'
                   AND locked_until <= clock_timestamp()
                   AND locked_by IS DISTINCT FROM $1)
            ORDER BY
                CASE WHEN status = 'pending' THEN next_attempt_at ELSE locked_until END,
                event_sequence
            FOR UPDATE SKIP LOCKED
            LIMIT $2
        )
        UPDATE public.outbox_messages AS message
        SET status = 'processing',
            locked_by = $1,
            lock_generation = message.lock_generation + 1,
            publish_attempts = message.publish_attempts + 1,
            locked_until = clock_timestamp() + $3
        FROM candidates
        WHERE message.id = candidates.id
        RETURNING message.id,
                  message.event_sequence,
                  message.deduplication_key,
                  message.topic,
                  message.schema_version,
                  message.aggregate_type,
                  message.aggregate_id,
                  message.aggregate_version,
                  message.event_type,
                  message.source_event_sequence,
                  message.correlation_id,
                  message.causation_id,
                  message.payload::text,
                  message.occurred_at,
                  message.replay_of,
                  message.lock_generation,
                  message.publish_attempts;
        """;

    private const string HeartbeatSql = """
        UPDATE public.outbox_messages
        SET locked_until = clock_timestamp() + $5
        WHERE id = $1
          AND status = 'processing'
          AND locked_by = $2
          AND lock_generation = $3
          AND publish_attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string MarkPublishedSql = """
        UPDATE public.outbox_messages
        SET status = 'published',
            next_attempt_at = NULL,
            locked_by = NULL,
            locked_until = NULL,
            published_at = clock_timestamp(),
            last_error = NULL
        WHERE id = $1
          AND status = 'processing'
          AND locked_by = $2
          AND lock_generation = $3
          AND publish_attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string RetrySql = """
        UPDATE public.outbox_messages
        SET status = 'pending',
            next_attempt_at = clock_timestamp() + $5,
            locked_by = NULL,
            locked_until = NULL,
            last_error = $6
        WHERE id = $1
          AND status = 'processing'
          AND locked_by = $2
          AND lock_generation = $3
          AND publish_attempts = $4
          AND locked_until > clock_timestamp();
        """;

    private const string DeadSql = """
        UPDATE public.outbox_messages
        SET status = 'dead',
            next_attempt_at = NULL,
            locked_by = NULL,
            locked_until = NULL,
            dead_at = clock_timestamp(),
            last_error = $5
        WHERE id = $1
          AND status = 'processing'
          AND locked_by = $2
          AND lock_generation = $3
          AND publish_attempts = $4
          AND locked_until > clock_timestamp();
        """;

    public async ValueTask<IReadOnlyList<OutboxDeliveryMessage>> ClaimDueAsync(
        OutboxClaimRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ClaimRoutedSql);
        command.Parameters.AddWithValue(request.Topics.ToArray());
        command.Parameters.AddWithValue(request.Owner.Value);
        command.Parameters.AddWithValue(request.MaximumCount);
        command.Parameters.AddWithValue(request.LeaseDuration);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<OutboxDeliveryMessage> messages = new(request.MaximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            OutboxMessageEnvelope envelope = ReadEnvelope(reader, request.Owner);
            messages.Add(new OutboxDeliveryMessage(
                envelope,
                CreatePartitionKey(
                    envelope.Topic,
                    envelope.AggregateType,
                    envelope.AggregateId),
                envelope.SourceEventSequence,
                reader.GetBoolean(17)));
        }

        return messages;
    }

    public async ValueTask<IReadOnlyList<OutboxMessageEnvelope>> ClaimDueAsync(
        EntityId owner,
        int maximumCount,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        if (maximumCount is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        PostgresPersistenceGuard.Positive(leaseDuration, nameof(leaseDuration));
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ClaimSql);
        command.Parameters.AddWithValue(owner.Value);
        command.Parameters.AddWithValue(maximumCount);
        command.Parameters.AddWithValue(leaseDuration);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<OutboxMessageEnvelope> messages = new(maximumCount);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadEnvelope(reader, owner));
        }

        return messages;
    }

    public async ValueTask<bool> HeartbeatAsync(
        OutboxDeliveryLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        PostgresPersistenceGuard.Positive(leaseDuration, nameof(leaseDuration));
        return await ExecuteLeaseUpdateAsync(
            HeartbeatSql,
            lease,
            unitOfWorkContext,
            command => command.Parameters.AddWithValue(leaseDuration),
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> MarkPublishedAsync(
        OutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        return ExecuteLeaseUpdateAsync(
            MarkPublishedSql,
            lease,
            unitOfWorkContext,
            null,
            cancellationToken);
    }

    public async ValueTask<bool> ReleaseForRetryAsync(
        OutboxDeliveryLease lease,
        TimeSpan retryDelay,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        PostgresPersistenceGuard.Positive(retryDelay, nameof(retryDelay));
        ValidateError(errorSummary);
        return await ExecuteLeaseUpdateAsync(
            RetrySql,
            lease,
            unitOfWorkContext,
            command =>
            {
                command.Parameters.AddWithValue(retryDelay);
                command.Parameters.AddWithValue(errorSummary);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> MarkDeadAsync(
        OutboxDeliveryLease lease,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        Validate(lease);
        ValidateError(errorSummary);
        return await ExecuteLeaseUpdateAsync(
            DeadSql,
            lease,
            unitOfWorkContext,
            command => command.Parameters.AddWithValue(errorSummary),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> ExecuteLeaseUpdateAsync(
        string sql,
        OutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        Action<NpgsqlCommand>? addParameters,
        CancellationToken cancellationToken)
    {
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(sql);
        command.Parameters.AddWithValue(lease.MessageId.Value);
        command.Parameters.AddWithValue(lease.Owner.Value);
        command.Parameters.AddWithValue(lease.Generation);
        command.Parameters.AddWithValue(lease.Attempt);
        addParameters?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OutboxMessageEnvelope ReadEnvelope(
        NpgsqlDataReader reader,
        EntityId owner)
    {
        EntityId messageId = new(reader.GetGuid(0));
        return new OutboxMessageEnvelope(
            new OutboxDeliveryLease(
                messageId,
                owner,
                reader.GetInt64(15),
                reader.GetInt32(16)),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            new EntityId(reader.GetGuid(6)),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetInt64(9),
            new EntityId(reader.GetGuid(10)),
            reader.IsDBNull(11) ? null : new EntityId(reader.GetGuid(11)),
            ParseJson(reader.GetString(12)),
            new DateTimeOffset(reader.GetFieldValue<DateTime>(13).ToUniversalTime()),
            reader.IsDBNull(14) ? null : new EntityId(reader.GetGuid(14)));
    }

    private static string CreatePartitionKey(
        string topic,
        string aggregateType,
        EntityId aggregateId)
    {
        string lowerAggregateId = aggregateId.Value
            .ToString("D", CultureInfo.InvariantCulture)
            .ToLowerInvariant();
        string partitionType = string.Equals(topic, QuotaTopic, StringComparison.Ordinal)
            ? "group"
            : aggregateType;
        return $"{topic}:{partitionType}:{lowerAggregateId}";
    }

    private static void Validate(OutboxDeliveryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Generation <= 0 || lease.Attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }
    }

    private static void ValidateError(string errorSummary)
    {
        PostgresPersistenceGuard.NotBlank(errorSummary, nameof(errorSummary));
        if (errorSummary.Length > 2048)
        {
            throw new ArgumentException("The non-secret error summary is too long.", nameof(errorSummary));
        }
    }
}
