using System.Numerics;
using Npgsql;
using NpgsqlTypes;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresGroupQuotaReconciliationFactReader :
    IGroupQuotaReconciliationFactReader
{
    private const int MaximumPageSize = 1000;
    private static readonly BigInteger MaximumAdministrativeTotal =
        new(9_007_199_254_740_991L);
    private static readonly BigInteger MaximumAggregateTokenCount =
        BigInteger.Pow(10, 78) - BigInteger.One;

    private const string ReadSql = """
        WITH reconciliation_clock AS MATERIALIZED (
            SELECT clock_timestamp() AS checked_at
        ),
        selected_period AS MATERIALIZED (
            SELECT
                period.id,
                period.group_id,
                period.total_tokens,
                period.consumed_tokens,
                period.reserved_tokens,
                period.status = 'current' AS is_current_period
            FROM public.group_token_quotas AS quota
            JOIN public.group_quota_periods AS period
              ON period.group_id = quota.group_id
             AND period.id = coalesce($2::uuid, quota.current_period_id)
            WHERE quota.group_id = $1
        ),
        settlement_facts AS MATERIALIZED (
            SELECT coalesce(
                       sum(coalesce(
                           adjustment.corrected_total_tokens,
                           attempt.total_tokens)),
                       0::numeric) AS consumed_tokens
            FROM selected_period AS period
            LEFT JOIN public.group_token_reservations AS reservation
              ON reservation.period_id = period.id
             AND reservation.group_id = period.group_id
            LEFT JOIN public.usage_attempts AS attempt
              ON attempt.reservation_id = reservation.id
             AND attempt.attempt_id = reservation.attempt_id
             AND attempt.request_id = reservation.request_id
             AND attempt.attempt_index = reservation.attempt_index
             AND attempt.quota_group_id = reservation.group_id
            LEFT JOIN public.usage_attempt_adjustments AS adjustment
              ON adjustment.attempt_id = attempt.attempt_id
        ),
        pending_reservations AS MATERIALIZED (
            SELECT
                coalesce(sum(reservation.estimated_tokens), 0::numeric)
                    AS reserved_tokens,
                count(reservation.id) AS reservation_count,
                count(*) FILTER (
                    WHERE reservation.lease_expires_at
                        < clock.checked_at - interval '60 seconds'
                ) AS overdue_count,
                min(reservation.lease_expires_at) FILTER (
                    WHERE reservation.lease_expires_at
                        < clock.checked_at - interval '60 seconds'
                ) AS oldest_overdue_at
            FROM selected_period AS period
            CROSS JOIN reconciliation_clock AS clock
            LEFT JOIN public.group_token_reservations AS reservation
              ON reservation.period_id = period.id
             AND reservation.group_id = period.group_id
             AND reservation.status = 'pending'
        ),
        fact_event_coverage AS MATERIALIZED (
            SELECT
                NOT EXISTS (
                    SELECT 1
                    FROM selected_period AS period
                    JOIN public.group_token_reservations AS reservation
                      ON reservation.period_id = period.id
                     AND reservation.group_id = period.group_id
                    JOIN public.usage_attempts AS attempt
                      ON attempt.reservation_id = reservation.id
                    LEFT JOIN LATERAL (
                        SELECT count(*) AS event_count
                        FROM public.group_quota_events AS event
                        WHERE event.group_id = period.group_id
                          AND event.period_id = period.id
                          AND event.reservation_id = reservation.id
                          AND event.attempt_id = attempt.attempt_id
                          AND event.event_type IN ('settled', 'expired')
                    ) AS terminal_event ON true
                    LEFT JOIN public.usage_attempt_adjustments AS adjustment
                      ON adjustment.attempt_id = attempt.attempt_id
                    LEFT JOIN public.group_quota_events AS adjustment_event
                      ON adjustment_event.id = adjustment.quota_event_id
                     AND adjustment_event.group_id = period.group_id
                     AND adjustment_event.period_id = period.id
                     AND adjustment_event.reservation_id = reservation.id
                     AND adjustment_event.attempt_id = attempt.attempt_id
                     AND adjustment_event.event_type = 'usage_adjusted'
                    WHERE attempt.attempt_id <> reservation.attempt_id
                       OR attempt.request_id <> reservation.request_id
                       OR attempt.attempt_index <> reservation.attempt_index
                       OR attempt.quota_group_id <> reservation.group_id
                       OR terminal_event.event_count <> 1
                       OR adjustment.attempt_id IS NOT NULL
                          AND adjustment_event.id IS NULL
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM selected_period AS period
                    JOIN public.group_quota_events AS event
                      ON event.group_id = period.group_id
                     AND event.period_id = period.id
                     AND event.event_type IN ('settled', 'expired')
                    LEFT JOIN public.group_token_reservations AS reservation
                      ON reservation.id = event.reservation_id
                     AND reservation.period_id = event.period_id
                     AND reservation.group_id = event.group_id
                     AND reservation.attempt_id = event.attempt_id
                    LEFT JOIN public.usage_attempts AS attempt
                      ON attempt.attempt_id = event.attempt_id
                     AND attempt.reservation_id = event.reservation_id
                     AND attempt.request_id = reservation.request_id
                     AND attempt.attempt_index = reservation.attempt_index
                     AND attempt.quota_group_id = event.group_id
                    WHERE reservation.id IS NULL OR attempt.attempt_id IS NULL
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM selected_period AS period
                    JOIN public.group_quota_events AS event
                      ON event.group_id = period.group_id
                     AND event.period_id = period.id
                     AND event.event_type = 'usage_adjusted'
                    LEFT JOIN public.usage_attempt_adjustments AS adjustment
                      ON adjustment.quota_event_id = event.id
                     AND adjustment.attempt_id = event.attempt_id
                    WHERE adjustment.attempt_id IS NULL
                ) AS is_consistent
        ),
        ordered_events AS MATERIALIZED (
            SELECT
                event.event_sequence,
                event.delta_total_tokens,
                event.delta_consumed_tokens,
                event.delta_reserved_tokens,
                event.total_tokens_after,
                event.consumed_tokens_after,
                event.reserved_tokens_after,
                event.occurred_at,
                lag(event.total_tokens_after) OVER event_order AS prior_total_tokens,
                lag(event.consumed_tokens_after) OVER event_order
                    AS prior_consumed_tokens,
                lag(event.reserved_tokens_after) OVER event_order
                    AS prior_reserved_tokens
            FROM selected_period AS period
            JOIN public.group_quota_events AS event
              ON event.period_id = period.id
             AND event.group_id = period.group_id
            WINDOW event_order AS (ORDER BY event.event_sequence)
        ),
        event_chain AS MATERIALIZED (
            SELECT
                count(*) AS event_count,
                min(event_sequence) AS first_event_sequence,
                coalesce(
                    bool_and(
                        CASE
                            WHEN prior_total_tokens IS NULL THEN
                                total_tokens_after = delta_total_tokens
                                AND consumed_tokens_after = delta_consumed_tokens
                                AND reserved_tokens_after = delta_reserved_tokens
                            ELSE total_tokens_after
                                    = prior_total_tokens + delta_total_tokens
                              AND consumed_tokens_after
                                    = prior_consumed_tokens + delta_consumed_tokens
                              AND reserved_tokens_after
                                    = prior_reserved_tokens + delta_reserved_tokens
                        END),
                    false) AS is_consistent
            FROM ordered_events
        ),
        latest_event AS MATERIALIZED (
            SELECT
                event_sequence,
                total_tokens_after,
                consumed_tokens_after,
                reserved_tokens_after,
                occurred_at
            FROM ordered_events
            ORDER BY event_sequence DESC
            LIMIT 1
        ),
        latest_group_event AS MATERIALIZED (
            SELECT event.event_sequence
            FROM selected_period AS period
            JOIN public.group_quota_events AS event
              ON event.group_id = period.group_id
            ORDER BY event.event_sequence DESC
            LIMIT 1
        ),
        checkpoint_event AS MATERIALIZED (
            SELECT event.consumed_tokens_after
            FROM ordered_events AS event
            WHERE $3 > 0
              AND event.event_sequence <= $3
            ORDER BY event.event_sequence DESC
            LIMIT 1
        ),
        checkpoint_position AS MATERIALIZED (
            SELECT $3 = 0 OR EXISTS (
                SELECT 1
                FROM public.group_quota_events AS event
                WHERE event.group_id = period.group_id
                  AND event.event_sequence = $3
            ) AS belongs_to_group
            FROM selected_period AS period
        )
        SELECT
            period.group_id,
            period.id,
            period.total_tokens,
            period.consumed_tokens,
            period.reserved_tokens,
            facts.consumed_tokens,
            pending.reserved_tokens,
            pending.reservation_count,
            pending.overdue_count,
            pending.oldest_overdue_at,
            coalesce(checkpoint.consumed_tokens_after, 0::numeric)
                AS checkpoint_consumed_tokens,
            checkpoint_position.belongs_to_group,
            latest.event_sequence,
            latest.occurred_at,
            chain.is_consistent,
            coverage.is_consistent,
            latest.total_tokens_after = period.total_tokens
                AND latest.consumed_tokens_after = period.consumed_tokens
                AND latest.reserved_tokens_after = period.reserved_tokens
                AS latest_event_matches_ledger,
            greatest(period.consumed_tokens - period.total_tokens, 0::numeric)
                AS overage_tokens,
            clock.checked_at,
            period.is_current_period,
            chain.first_event_sequence,
            latest_group.event_sequence AS latest_group_event_sequence,
            chain.event_count
        FROM selected_period AS period
        CROSS JOIN settlement_facts AS facts
        CROSS JOIN pending_reservations AS pending
        CROSS JOIN event_chain AS chain
        CROSS JOIN fact_event_coverage AS coverage
        CROSS JOIN reconciliation_clock AS clock
        CROSS JOIN checkpoint_position
        LEFT JOIN latest_event AS latest ON true
        LEFT JOIN latest_group_event AS latest_group ON true
        LEFT JOIN checkpoint_event AS checkpoint ON true;
        """;

    private const string ListCurrentCandidatesSql = """
        SELECT quota.group_id, quota.current_period_id
        FROM public.group_token_quotas AS quota
        WHERE $1::uuid IS NULL OR quota.group_id > $1
        ORDER BY quota.group_id
        LIMIT $2;
        """;

    private const string ListPeriodSourceEventSequencesSql = """
        SELECT event.event_sequence
        FROM public.group_quota_events AS event
        WHERE event.group_id = $1
          AND event.period_id = $2
          AND event.event_sequence <= $3
          AND event.event_sequence > $4
        ORDER BY event.event_sequence
        LIMIT $5;
        """;

    public async ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
        EntityId groupId,
        EntityId? periodId,
        long checkpointSourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ValidateId(groupId, nameof(groupId));
        if (periodId is { } requestedPeriod)
        {
            ValidateId(requestedPeriod, nameof(periodId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(checkpointSourceEventSequence);
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReadSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = periodId is { } value ? value.Value : DBNull.Value,
        });
        command.Parameters.AddWithValue(checkpointSourceEventSequence);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        GroupQuotaReconciliationFactSnapshot snapshot = ReadAbi(
            () => ReadSnapshot(reader, checkpointSourceEventSequence),
            "The PostgreSQL Group quota reconciliation fact violated its ABI.");

        ValidateSingleSnapshot(
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        ValidateSnapshot(snapshot);
        return snapshot;
    }

    public async ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
        ListCurrentCandidatesAsync(
            EntityId? afterGroupId,
            int maximumCount,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
    {
        if (afterGroupId is { } cursor)
        {
            ValidateId(cursor, nameof(afterGroupId));
        }

        if (maximumCount is <= 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ListCurrentCandidatesSql);
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = afterGroupId is { } value ? value.Value : DBNull.Value,
        });
        command.Parameters.AddWithValue(maximumCount);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<GroupQuotaReconciliationCandidate> candidates = [];
        EntityId? prior = afterGroupId;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            GroupQuotaReconciliationCandidate candidate = ReadCandidate(reader);
            ValidateCandidate(candidate, prior);
            candidates.Add(candidate);
            prior = candidate.GroupId;
        }

        ValidatePageCount(
            candidates.Count,
            maximumCount,
            "The PostgreSQL Group quota reconciliation candidate page exceeded its bound.");

        return candidates;
    }

    public async ValueTask<IReadOnlyList<long>>
        ListPeriodSourceEventSequencesAsync(
            EntityId groupId,
            EntityId periodId,
            long throughSourceEventSequence,
            long afterSourceEventSequence,
            int maximumCount,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
    {
        ValidateSequencePageRequest(
            groupId,
            periodId,
            throughSourceEventSequence,
            afterSourceEventSequence,
            maximumCount);
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(
            ListPeriodSourceEventSequencesSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(throughSourceEventSequence);
        command.Parameters.AddWithValue(afterSourceEventSequence);
        command.Parameters.AddWithValue(maximumCount);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<long> sourceEventSequences = [];
        long prior = afterSourceEventSequence;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long sourceEventSequence = ReadAbi(
                () => reader.GetInt64(0),
                "The PostgreSQL Group quota event sequence violated its ABI.");
            ValidateSourceEventSequence(
                sourceEventSequence,
                prior,
                throughSourceEventSequence);

            sourceEventSequences.Add(sourceEventSequence);
            prior = sourceEventSequence;
        }

        ValidatePageCount(
            sourceEventSequences.Count,
            maximumCount,
            "The PostgreSQL Group quota event sequence page exceeded its bound.");

        return sourceEventSequences;
    }

    private static void ValidateSequencePageRequest(
        EntityId groupId,
        EntityId periodId,
        long throughSourceEventSequence,
        long afterSourceEventSequence,
        int maximumCount)
    {
        ValidateId(groupId, nameof(groupId));
        ValidateId(periodId, nameof(periodId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            throughSourceEventSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSourceEventSequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            afterSourceEventSequence,
            throughSourceEventSequence);
        if (maximumCount is <= 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }
    }

    private static GroupQuotaReconciliationCandidate ReadCandidate(
        NpgsqlDataReader reader) => ReadAbi(
            () => new GroupQuotaReconciliationCandidate(
                new EntityId(reader.GetGuid(0)),
                new EntityId(reader.GetGuid(1))),
            "The PostgreSQL Group quota reconciliation candidate violated its ABI.");

    internal static void ValidateCandidate(
        GroupQuotaReconciliationCandidate candidate,
        EntityId? prior)
    {
        ValidateId(candidate.GroupId, nameof(candidate.GroupId));
        ValidateId(candidate.PeriodId, nameof(candidate.PeriodId));
        if (prior is { } previous && Compare(candidate.GroupId, previous) <= 0)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Group quota reconciliation candidates were not a strict keyset page.");
        }
    }

    private static GroupQuotaReconciliationFactSnapshot ReadSnapshot(
        NpgsqlDataReader reader,
        long checkpointSourceEventSequence) => new(
            new EntityId(reader.GetGuid(0)),
            new EntityId(reader.GetGuid(1)),
            checkpointSourceEventSequence,
            reader.GetFieldValue<BigInteger>(2),
            reader.GetFieldValue<BigInteger>(3),
            reader.GetFieldValue<BigInteger>(4),
            reader.GetFieldValue<BigInteger>(5),
            reader.GetFieldValue<BigInteger>(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            ReadTimestamp(reader, 9),
            reader.GetFieldValue<BigInteger>(10),
            reader.GetBoolean(11),
            reader.GetInt64(12),
            RequireTimestamp(reader, 13),
            reader.GetBoolean(14),
            reader.GetBoolean(15),
            reader.GetBoolean(16),
            reader.GetFieldValue<BigInteger>(17),
            RequireTimestamp(reader, 18),
            reader.GetBoolean(19),
            reader.GetInt64(20),
            reader.GetInt64(21),
            reader.GetInt64(22));

    internal static void ValidateSnapshot(GroupQuotaReconciliationFactSnapshot snapshot)
    {
        ValidateId(snapshot.GroupId, nameof(snapshot.GroupId));
        ValidateId(snapshot.PeriodId, nameof(snapshot.PeriodId));
        if (snapshot.CheckpointSourceEventSequence < 0
            || snapshot.LedgerTotalTokens < BigInteger.One
            || snapshot.LedgerTotalTokens > MaximumAdministrativeTotal
            || snapshot.LedgerConsumedTokens < BigInteger.Zero
            || snapshot.LedgerConsumedTokens > MaximumAggregateTokenCount
            || snapshot.LedgerReservedTokens < BigInteger.Zero
            || snapshot.LedgerReservedTokens > MaximumAggregateTokenCount
            || snapshot.FactConsumedTokens < BigInteger.Zero
            || snapshot.FactConsumedTokens > MaximumAggregateTokenCount
            || snapshot.PendingReservationTokens < BigInteger.Zero
            || snapshot.PendingReservationTokens > MaximumAggregateTokenCount
            || snapshot.PendingReservationCount < 0
            || (snapshot.PendingReservationCount == 0)
                != (snapshot.PendingReservationTokens == BigInteger.Zero)
            || snapshot.OverdueReservationCount < 0
            || snapshot.OverdueReservationCount > snapshot.PendingReservationCount
            || snapshot.ExpectedConsumedAtCheckpoint < BigInteger.Zero
            || snapshot.ExpectedConsumedAtCheckpoint > MaximumAggregateTokenCount
            || (snapshot.CheckpointSourceEventSequence == 0
                && snapshot.ExpectedConsumedAtCheckpoint != BigInteger.Zero)
            || snapshot.LatestPeriodEventSequence <= 0
            || snapshot.FirstPeriodEventSequence <= 0
            || snapshot.FirstPeriodEventSequence > snapshot.LatestPeriodEventSequence
            || snapshot.LatestGroupEventSequence < snapshot.LatestPeriodEventSequence
            || snapshot.PeriodEventCount <= 0
            || snapshot.OverageTokens < BigInteger.Zero
            || snapshot.OverageTokens > MaximumAggregateTokenCount
            || snapshot.OverageTokens != BigInteger.Max(
                snapshot.LedgerConsumedTokens - snapshot.LedgerTotalTokens,
                BigInteger.Zero)
            || snapshot.CheckedAt < DateTimeOffset.UnixEpoch
            || snapshot.CheckedAt.Offset != TimeSpan.Zero
            || snapshot.LatestPeriodEventOccurredAt < DateTimeOffset.UnixEpoch
            || snapshot.LatestPeriodEventOccurredAt.Offset != TimeSpan.Zero
            || snapshot.LatestPeriodEventOccurredAt > snapshot.CheckedAt
            || (snapshot.OverdueReservationCount == 0)
                != (snapshot.OldestOverdueAt is null)
            || (snapshot.OldestOverdueAt is { } oldestOverdueAt
                && (oldestOverdueAt < DateTimeOffset.UnixEpoch
                    || oldestOverdueAt.Offset != TimeSpan.Zero
                    || oldestOverdueAt >= snapshot.CheckedAt.AddSeconds(-60))))
        {
            throw new InvalidOperationException(
                "The PostgreSQL Group quota reconciliation fact violated its ABI.");
        }
    }

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : RequireTimestamp(reader, ordinal);

    private static DateTimeOffset RequireTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.GetFieldValue<DateTimeOffset>(ordinal);

    internal static void ValidateSourceEventSequence(
        long sourceEventSequence,
        long prior,
        long throughSourceEventSequence)
    {
        if (sourceEventSequence <= prior
            || sourceEventSequence > throughSourceEventSequence)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Group quota event sequences were not a strict keyset page.");
        }
    }

    internal static void ValidatePageCount(
        int actualCount,
        int maximumCount,
        string message)
    {
        if (actualCount > maximumCount)
        {
            throw new InvalidOperationException(message);
        }
    }

    internal static void ValidateSingleSnapshot(bool hasAdditionalSnapshot)
    {
        if (hasAdditionalSnapshot)
        {
            throw new InvalidOperationException(
                "The PostgreSQL Group quota reconciliation query returned duplicate periods.");
        }
    }

    internal static T ReadAbi<T>(Func<T> read, string message)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException(message, exception);
        }
    }

    private static void ValidateId(EntityId entityId, string parameterName)
    {
        if (entityId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The entity identifier must be a non-empty UUID.",
                parameterName);
        }
    }

    private static int Compare(EntityId left, EntityId right) =>
        StringComparer.Ordinal.Compare(left.Value.ToString("N"), right.Value.ToString("N"));
}
