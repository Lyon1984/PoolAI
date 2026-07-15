using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresOutboxDeliveryStore : IOutboxDeliveryStore
{
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

    private const string ReplaySql = """
        INSERT INTO public.outbox_messages (
            id, deduplication_key, topic, schema_version, aggregate_type,
            aggregate_id, aggregate_version, event_type, source_event_sequence,
            correlation_id, causation_id, payload, occurred_at, replay_of
        )
        SELECT $2,
               $3,
               source.topic,
               source.schema_version,
               source.aggregate_type,
               source.aggregate_id,
               source.aggregate_version,
               source.event_type,
               source.source_event_sequence,
               source.correlation_id,
               source.causation_id,
               source.payload,
               source.occurred_at,
               source.id
        FROM public.outbox_messages AS source
        WHERE source.id = $1 AND source.status = 'dead'
        RETURNING id, event_sequence;
        """;

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
            EntityId messageId = new(reader.GetGuid(0));
            messages.Add(new OutboxMessageEnvelope(
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
                reader.IsDBNull(14) ? null : new EntityId(reader.GetGuid(14))));
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

    public async ValueTask<OutboxReplayReceipt?> ReplayDeadAsync(
        OutboxReplayRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PostgresPersistenceGuard.NotBlank(
            request.NewDeduplicationKey,
            nameof(request.NewDeduplicationKey));
        if (request.DeadMessageId == request.NewMessageId)
        {
            throw new ArgumentException("A replay must have a new message identifier.", nameof(request));
        }

        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReplaySql);
        command.Parameters.AddWithValue(request.DeadMessageId.Value);
        command.Parameters.AddWithValue(request.NewMessageId.Value);
        command.Parameters.AddWithValue(request.NewDeduplicationKey);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new OutboxReplayReceipt(new EntityId(reader.GetGuid(0)), reader.GetInt64(1))
            : null;
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
