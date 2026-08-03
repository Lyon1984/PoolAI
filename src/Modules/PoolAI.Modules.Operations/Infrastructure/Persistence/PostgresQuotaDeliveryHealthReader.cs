using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed class PostgresQuotaDeliveryHealthReader : IQuotaDeliveryHealthReader
{
    private const int MaximumPageSize = 1000;

    private const string ReadSql = """
        WITH reconciliation_clock AS MATERIALIZED (
            SELECT clock_timestamp() AS checked_at
        ),
        expected_sequences AS MATERIALIZED (
            SELECT expected.source_event_sequence
            FROM unnest($2::bigint[]) WITH ORDINALITY
                AS expected(source_event_sequence, ordinal)
            ORDER BY expected.ordinal
        ),
        matched_messages AS MATERIALIZED (
            SELECT expected.source_event_sequence,
                   message.id,
                   message.event_sequence,
                   message.topic,
                   message.schema_version,
                   message.status,
                   message.replay_of,
                   message.occurred_at
            FROM expected_sequences AS expected
            JOIN LATERAL (
                SELECT unresolved.id,
                       unresolved.event_sequence,
                       unresolved.topic,
                       unresolved.schema_version,
                       unresolved.status,
                       unresolved.replay_of,
                       unresolved.occurred_at
                FROM public.outbox_messages AS unresolved
                WHERE unresolved.topic = 'poolai.quota.v1'
                  AND unresolved.aggregate_type = 'group'
                  AND unresolved.aggregate_id = $1
                  AND unresolved.source_event_sequence
                      = expected.source_event_sequence
                  AND unresolved.status <> 'published'
            ) AS message ON true

            UNION ALL

            SELECT expected.source_event_sequence,
                   message.id,
                   message.event_sequence,
                   message.topic,
                   message.schema_version,
                   message.status,
                   message.replay_of,
                   message.occurred_at
            FROM expected_sequences AS expected
            JOIN LATERAL (
                SELECT published.id,
                       published.event_sequence,
                       published.topic,
                       published.schema_version,
                       published.status,
                       published.replay_of,
                       published.occurred_at
                FROM public.outbox_messages AS published
                WHERE published.topic = 'poolai.quota.v1'
                  AND published.aggregate_type = 'group'
                  AND published.aggregate_id = $1
                  AND coalesce(published.source_event_sequence, 0)
                      = expected.source_event_sequence
                  AND published.status = 'published'
            ) AS message ON true
        ),
        scoped_messages AS MATERIALIZED (
            SELECT expected.source_event_sequence,
                   message.id,
                   message.event_sequence,
                   message.topic,
                   message.schema_version,
                   message.status,
                   message.replay_of,
                   message.occurred_at
            FROM expected_sequences AS expected
            LEFT JOIN matched_messages AS message
              ON message.source_event_sequence = expected.source_event_sequence
        ),
        receipt_evidence AS MATERIALIZED (
            SELECT message.*,
                   receipt_by_message.message_id IS NOT NULL
                       AND message.schema_version = 1
                       AND receipt_by_message.topic = message.topic
                       AND receipt_by_message.event_sequence
                           = message.event_sequence
                       AND receipt_by_message.schema_version
                           = message.schema_version
                       AS has_exact_inbox_receipt,
                   (receipt_by_message.message_id IS NOT NULL
                        AND NOT (
                            message.schema_version = 1
                            AND receipt_by_message.topic = message.topic
                            AND receipt_by_message.event_sequence
                                = message.event_sequence
                            AND receipt_by_message.schema_version
                                = message.schema_version))
                       OR (receipt_by_sequence.message_id IS NOT NULL
                           AND receipt_by_sequence.message_id <> message.id)
                       AS has_conflicting_inbox_receipt
            FROM scoped_messages AS message
            LEFT JOIN public.inbox_messages AS receipt_by_message
              ON receipt_by_message.consumer_name = 'usage-hourly-v1'
             AND receipt_by_message.message_id = message.id
            LEFT JOIN public.inbox_messages AS receipt_by_sequence
              ON receipt_by_sequence.consumer_name = 'usage-hourly-v1'
             AND receipt_by_sequence.topic = message.topic
             AND receipt_by_sequence.event_sequence = message.event_sequence
        ),
        logical_lineages AS MATERIALIZED (
            SELECT message.source_event_sequence,
                   coalesce(bool_or(message.status = 'published'), false)
                       AS is_complete,
                   coalesce(bool_or(message.status = 'processing'), false)
                       AS has_processing,
                   coalesce(bool_or(message.status = 'pending'), false)
                       AS has_pending,
                   coalesce(bool_or(message.status = 'dead'), false)
                       AS has_dead,
                   message.source_event_sequence <= $3
                       AS expects_inbox_receipt,
                   coalesce(
                       bool_or(message.has_exact_inbox_receipt),
                       false) AS has_exact_inbox_receipt,
                   coalesce(
                       bool_or(message.has_conflicting_inbox_receipt),
                       false) AS has_conflicting_inbox_receipt,
                   count(message.id) FILTER (
                       WHERE message.replay_of IS NULL) AS original_count,
                   min(message.occurred_at) AS first_occurred_at
            FROM receipt_evidence AS message
            GROUP BY message.source_event_sequence
        ),
        summary AS (
            SELECT coalesce(sum(lineage.original_count), 0)::bigint AS original_count,
                   count(*) FILTER (
                       WHERE lineage.original_count = 0)::bigint
                       AS missing_original_count,
                   coalesce(sum(greatest(lineage.original_count - 1, 0)), 0)::bigint
                       AS duplicate_original_count,
                   count(*) FILTER (
                       WHERE NOT lineage.is_complete
                         AND NOT lineage.has_processing
                         AND lineage.has_pending
                   )::bigint AS pending_lineage_count,
                   count(*) FILTER (
                       WHERE NOT lineage.is_complete
                         AND lineage.has_processing
                   )::bigint AS processing_lineage_count,
                   count(*) FILTER (
                       WHERE NOT lineage.is_complete
                         AND NOT lineage.has_processing
                         AND NOT lineage.has_pending
                         AND lineage.has_dead
                   )::bigint AS dead_lineage_count,
                   count(*) FILTER (
                       WHERE lineage.expects_inbox_receipt
                   )::bigint AS expected_inbox_receipt_count,
                   count(*) FILTER (
                       WHERE lineage.expects_inbox_receipt
                         AND NOT lineage.has_exact_inbox_receipt
                         AND NOT lineage.has_conflicting_inbox_receipt
                   )::bigint AS missing_inbox_receipt_count,
                   count(*) FILTER (
                       WHERE lineage.expects_inbox_receipt
                         AND lineage.has_conflicting_inbox_receipt
                   )::bigint AS conflicting_inbox_receipt_count,
                   min(lineage.first_occurred_at) FILTER (
                       WHERE NOT lineage.is_complete
                          OR lineage.expects_inbox_receipt
                             AND (NOT lineage.has_exact_inbox_receipt
                                  OR lineage.has_conflicting_inbox_receipt)
                   ) AS oldest_unresolved_at,
                   min(lineage.source_event_sequence) FILTER (
                       WHERE lineage.original_count <> 1
                          OR NOT lineage.is_complete
                          OR lineage.expects_inbox_receipt
                             AND (NOT lineage.has_exact_inbox_receipt
                                  OR lineage.has_conflicting_inbox_receipt)
                   ) AS blocking_source_event_sequence
            FROM logical_lineages AS lineage
        )
        SELECT summary.original_count,
               summary.missing_original_count,
               summary.duplicate_original_count,
               summary.pending_lineage_count,
               summary.processing_lineage_count,
               summary.dead_lineage_count,
               summary.expected_inbox_receipt_count,
               summary.missing_inbox_receipt_count,
               summary.conflicting_inbox_receipt_count,
               greatest(
                   coalesce(
                       extract(
                           epoch FROM clock.checked_at - summary.oldest_unresolved_at),
                       0),
                   0)::double precision AS oldest_unresolved_age_seconds,
               summary.blocking_source_event_sequence,
               clock.checked_at
        FROM summary
        CROSS JOIN reconciliation_clock AS clock;
        """;

    public async ValueTask<QuotaDeliveryHealthSnapshot> ReadAsync(
        EntityId groupId,
        IReadOnlyList<long> expectedSourceEventSequences,
        long checkpointSourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        long[] expectedSequences = ValidateRequest(
            groupId,
            expectedSourceEventSequences,
            checkpointSourceEventSequence);
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = CreateCommand(
            session,
            groupId,
            expectedSequences,
            checkpointSourceEventSequence);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        QuotaDeliveryHealthSnapshot snapshot = await ReadSingleSnapshotAsync(
            reader,
            cancellationToken).ConfigureAwait(false);
        ValidateSnapshot(
            snapshot,
            expectedSequences,
            checkpointSourceEventSequence);
        return snapshot;
    }

    private static async ValueTask<QuotaDeliveryHealthSnapshot> ReadSingleSnapshotAsync(
        NpgsqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health query returned no summary.");
        }

        QuotaDeliveryHealthSnapshot snapshot = ReadSnapshot(reader);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health query returned duplicate summaries.");
        }

        return snapshot;
    }

    private static QuotaDeliveryHealthSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        try
        {
            return new QuotaDeliveryHealthSnapshot(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetDouble(9),
                reader.IsDBNull(10) ? null : reader.GetInt64(10),
                reader.GetFieldValue<DateTimeOffset>(11));
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health snapshot violated its ABI.",
                exception);
        }
    }

    private static NpgsqlCommand CreateCommand(
        PostgresTransactionSession session,
        EntityId groupId,
        long[] expectedSourceEventSequences,
        long checkpointSourceEventSequence)
    {
        NpgsqlCommand command = session.CreateCommand(ReadSql);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, groupId.Value);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            expectedSourceEventSequences);
        command.Parameters.AddWithValue(
            NpgsqlDbType.Bigint,
            checkpointSourceEventSequence);
        return command;
    }

    private static long[] ValidateRequest(
        EntityId groupId,
        IReadOnlyList<long> expectedSourceEventSequences,
        long checkpointSourceEventSequence)
    {
        ValidateId(groupId, nameof(groupId));
        ArgumentOutOfRangeException.ThrowIfNegative(
            checkpointSourceEventSequence);
        ArgumentNullException.ThrowIfNull(expectedSourceEventSequences);
        if (expectedSourceEventSequences.Count is <= 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedSourceEventSequences));
        }

        long[] expectedSequences = new long[expectedSourceEventSequences.Count];
        long prior = 0;
        for (int index = 0; index < expectedSequences.Length; index++)
        {
            long sourceEventSequence = expectedSourceEventSequences[index];
            if (sourceEventSequence <= prior)
            {
                throw new ArgumentException(
                    "Expected source event sequences must be positive and strictly increasing.",
                    nameof(expectedSourceEventSequences));
            }

            expectedSequences[index] = sourceEventSequence;
            prior = sourceEventSequence;
        }

        return expectedSequences;
    }

    private static void ValidateSnapshot(
        QuotaDeliveryHealthSnapshot snapshot,
        long[] expectedSourceEventSequences,
        long checkpointSourceEventSequence)
    {
        long expectedOriginalCount = expectedSourceEventSequences.LongLength;
        long expectedObservedOriginalCount;
        try
        {
            expectedObservedOriginalCount = checked(
                expectedOriginalCount
                - snapshot.MissingOriginalCount
                + snapshot.DuplicateOriginalCount);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health snapshot violated its ABI.",
                exception);
        }

        long classifiedLineageCount;
        try
        {
            classifiedLineageCount = checked(
                snapshot.PendingLineageCount
                + snapshot.ProcessingLineageCount
                + snapshot.DeadLineageCount);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health snapshot violated its ABI.",
                exception);
        }

        if (snapshot.MissingOriginalCount > expectedOriginalCount
            || snapshot.OriginalCount != expectedObservedOriginalCount
            || classifiedLineageCount > expectedOriginalCount
            || snapshot.ExpectedInboxReceiptCount
                != expectedSourceEventSequences.LongCount(
                    sequence => sequence <= checkpointSourceEventSequence)
            || snapshot.BlockingSourceEventSequence is { } blockingSequence
                && Array.BinarySearch(
                    expectedSourceEventSequences,
                    blockingSequence) < 0)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota delivery-health snapshot violated its ABI.");
        }
    }

    private static void ValidateId(EntityId entityId, string parameterName)
    {
        if (entityId.Value == Guid.Empty || entityId.Value.Version != 7)
        {
            throw new ArgumentException(
                "The entity identifier must be a non-empty UUIDv7.",
                parameterName);
        }
    }
}
