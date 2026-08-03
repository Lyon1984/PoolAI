using System.Numerics;
using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresBoundedUsageRebuildFactReader :
    IBoundedUsageRebuildFactReader
{
    private const string ReadHourSql = """
        SELECT
            attempt.attempt_id,
            attempt.request_id,
            attempt.attempt_index,
            attempt.reservation_id,
            attempt.quota_group_id,
            reservation.period_id,
            attempt.account_id,
            attempt.channel_id,
            attempt.provider,
            attempt.model,
            attempt.status,
            attempt.routing_group_id,
            attempt.input_tokens,
            attempt.output_tokens,
            attempt.cache_read_tokens,
            attempt.cache_creation_tokens,
            attempt.thinking_tokens,
            attempt.usage_source,
            attempt.is_estimated,
            attempt.dispatch_started_at,
            attempt.first_token_at,
            attempt.completed_at,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.quota_event_id
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.previous_total_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.corrected_input_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.corrected_output_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.corrected_cache_read_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.corrected_cache_creation_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.corrected_thinking_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.usage_source
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.delta_tokens
            END,
            CASE
                WHEN adjustment.event_sequence <= $4
                THEN adjustment.adjusted_at
            END,
            usage_request.requested_model,
            usage_request.is_streaming,
            attempt.upstream_http_status,
            attempt.error_code,
            reservation.is_streaming,
            terminal.id,
            terminal.event_sequence,
            terminal.event_type,
            terminal.group_id,
            terminal.period_id,
            terminal.reservation_id,
            terminal.attempt_id,
            terminal.delta_consumed_tokens,
            terminal.matching_count,
            adjustment.quota_event_id,
            adjustment.event_sequence,
            adjustment.event_type,
            adjustment.group_id,
            adjustment.period_id,
            adjustment.reservation_id,
            adjustment.event_attempt_id,
            adjustment.event_delta_consumed_tokens,
            adjustment.delta_tokens,
            adjustment.matching_count,
            reservation.status
        FROM public.usage_attempts AS attempt
        JOIN public.group_token_reservations AS reservation
          ON reservation.id = attempt.reservation_id
         AND reservation.attempt_id = attempt.attempt_id
         AND reservation.request_id = attempt.request_id
         AND reservation.attempt_index = attempt.attempt_index
         AND reservation.group_id = attempt.quota_group_id
         AND reservation.period_id = $2
        JOIN public.usage_requests AS usage_request
          ON usage_request.request_id = attempt.request_id
         AND usage_request.quota_group_id = attempt.quota_group_id
         AND usage_request.routing_group_id = attempt.routing_group_id
        JOIN LATERAL (
            SELECT
                quota_event.id,
                quota_event.event_sequence,
                quota_event.event_type,
                quota_event.group_id,
                quota_event.period_id,
                quota_event.reservation_id,
                quota_event.attempt_id,
                quota_event.delta_consumed_tokens,
                count(*) OVER () AS matching_count
            FROM public.group_quota_events AS quota_event
            WHERE quota_event.attempt_id = attempt.attempt_id
              AND quota_event.event_type IN ('settled', 'expired')
              AND quota_event.event_sequence <= $4
            ORDER BY quota_event.event_sequence
            LIMIT 1
        ) AS terminal ON true
        LEFT JOIN LATERAL (
            SELECT
                fact_adjustment.quota_event_id,
                fact_adjustment.previous_total_tokens,
                fact_adjustment.corrected_input_tokens,
                fact_adjustment.corrected_output_tokens,
                fact_adjustment.corrected_cache_read_tokens,
                fact_adjustment.corrected_cache_creation_tokens,
                fact_adjustment.corrected_thinking_tokens,
                fact_adjustment.usage_source,
                fact_adjustment.delta_tokens,
                fact_adjustment.adjusted_at,
                adjustment_event.event_sequence,
                adjustment_event.event_type,
                adjustment_event.group_id,
                adjustment_event.period_id,
                adjustment_event.reservation_id,
                adjustment_event.attempt_id AS event_attempt_id,
                adjustment_event.delta_consumed_tokens
                    AS event_delta_consumed_tokens,
                count(*) OVER () AS matching_count
            FROM public.usage_attempt_adjustments AS fact_adjustment
            LEFT JOIN public.group_quota_events AS adjustment_event
              ON adjustment_event.id = fact_adjustment.quota_event_id
            WHERE fact_adjustment.attempt_id = attempt.attempt_id
            ORDER BY
                fact_adjustment.quota_event_id,
                adjustment_event.event_sequence NULLS LAST
            LIMIT 1
        ) AS adjustment ON true
        WHERE attempt.quota_group_id = $1
          AND attempt.completed_at >= $3
          AND attempt.completed_at < $3 + interval '1 hour'
        ORDER BY attempt.attempt_id;
        """;

    public async ValueTask<BoundedUsageRebuildHourSnapshot> ReadHourAsync(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        long checkpointSourceSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ValidateId(groupId, nameof(groupId));
        ValidateId(periodId, nameof(periodId));
        ValidateBucketStart(bucketStart);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointSourceSequence);
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReadHourSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(bucketStart);
        command.Parameters.AddWithValue(checkpointSourceSequence);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        List<AttemptSettlementFact> facts = [];
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            (AttemptSettlementFact fact, TerminalEvent terminal) = ReadAbi(() => (
                PostgresQuotaLedgerAbiContract.ReadAttemptFact(reader),
                ReadTerminalEvent(reader)));

            ValidateTerminalEvent(
                fact,
                terminal,
                checkpointSourceSequence,
                reader.GetString(56));
            ValidateAdjustmentEvent(
                reader,
                fact,
                terminal,
                checkpointSourceSequence);
            facts.Add(fact);
        }

        return ReadAbi(() => new BoundedUsageRebuildHourSnapshot(
                groupId,
                periodId,
                bucketStart,
                checkpointSourceSequence,
                facts));
    }

    private static TerminalEvent ReadTerminalEvent(NpgsqlDataReader reader) => new(
        new EntityId(reader.GetGuid(37)),
        reader.GetInt64(38),
        reader.GetString(39),
        new EntityId(reader.GetGuid(40)),
        new EntityId(reader.GetGuid(41)),
        new EntityId(reader.GetGuid(42)),
        new EntityId(reader.GetGuid(43)),
        reader.GetFieldValue<BigInteger>(44),
        reader.GetInt64(45));

    internal static void ValidateTerminalEvent(
        AttemptSettlementFact fact,
        TerminalEvent terminal,
        long checkpointSourceSequence,
        string reservationStatus)
    {
        if (terminal.MatchingCount != 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL bounded rebuild fact query returned duplicate terminal events.");
        }

        bool statusMatches = terminal.EventType switch
        {
            "settled" => string.Equals(
                reservationStatus,
                "settled",
                StringComparison.Ordinal),
            "expired" => string.Equals(
                    reservationStatus,
                    "expired",
                    StringComparison.Ordinal)
                && fact.Usage.Source == SettlementUsageSource.ConservativeEstimate
                && fact.Usage.IsEstimated,
            _ => false,
        };
        if (terminal.SourceSequence <= 0
            || terminal.SourceSequence > checkpointSourceSequence
            || terminal.GroupId != fact.GroupId
            || terminal.PeriodId != fact.PeriodId
            || terminal.ReservationId != fact.ReservationId
            || terminal.AttemptId != fact.AttemptId
            || terminal.DeltaConsumedTokens != fact.Usage.Tokens.TotalTokens
            || !statusMatches)
        {
            throw InvalidAbi();
        }
    }

    private static void ValidateAdjustmentEvent(
        NpgsqlDataReader reader,
        AttemptSettlementFact fact,
        TerminalEvent terminal,
        long checkpointSourceSequence)
    {
        if (reader.IsDBNull(46))
        {
            ValidateMissingAdjustment(
                Enumerable.Range(47, 9).Any(ordinal => !reader.IsDBNull(ordinal)),
                fact.Adjustment);
            return;
        }

        AdjustmentEvent adjustment = ReadAbi(() => new AdjustmentEvent(
                new EntityId(reader.GetGuid(46)),
                reader.GetInt64(47),
                reader.GetString(48),
                new EntityId(reader.GetGuid(49)),
                new EntityId(reader.GetGuid(50)),
                new EntityId(reader.GetGuid(51)),
                new EntityId(reader.GetGuid(52)),
                reader.GetFieldValue<BigInteger>(53),
                reader.GetFieldValue<BigInteger>(54),
                reader.GetInt64(55)));

        ValidateAdjustmentEvent(fact, terminal, adjustment, checkpointSourceSequence);
    }

    internal static void ValidateMissingAdjustment(
        bool hasUnexpectedEventColumn,
        AttemptUsageAdjustment? factAdjustment)
    {
        if (hasUnexpectedEventColumn || factAdjustment is not null)
        {
            throw InvalidAbi();
        }
    }

    internal static void ValidateAdjustmentEvent(
        AttemptSettlementFact fact,
        TerminalEvent terminal,
        AdjustmentEvent adjustment,
        long checkpointSourceSequence)
    {
        if (adjustment.MatchingCount != 1)
        {
            throw new InvalidOperationException(
                "The PostgreSQL bounded rebuild fact query returned duplicate adjustment identities.");
        }

        bool adjustmentIsVisible = adjustment.SourceSequence
            <= checkpointSourceSequence;
        if (adjustment.SourceSequence <= terminal.SourceSequence
            || !string.Equals(
                adjustment.EventType,
                "usage_adjusted",
                StringComparison.Ordinal)
            || adjustment.GroupId != fact.GroupId
            || adjustment.PeriodId != fact.PeriodId
            || adjustment.ReservationId != fact.ReservationId
            || adjustment.AttemptId != fact.AttemptId
            || adjustment.EventDeltaTokens != adjustment.FactDeltaTokens
            || adjustmentIsVisible != (fact.Adjustment is not null)
            || (adjustmentIsVisible
                && (fact.Adjustment!.QuotaEventId != adjustment.EventId
                    || fact.Adjustment.DeltaTokens != adjustment.FactDeltaTokens)))
        {
            throw InvalidAbi();
        }
    }

    private static void ValidateBucketStart(DateTimeOffset bucketStart)
    {
        if (bucketStart.Offset != TimeSpan.Zero
            || bucketStart.Minute != 0
            || bucketStart.Second != 0
            || bucketStart.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "The rebuild bucket must start on an exact UTC hour.",
                nameof(bucketStart));
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

    private static InvalidOperationException InvalidAbi(Exception? inner = null) => new(
        "The PostgreSQL bounded rebuild fact violated its ABI.",
        inner);

    internal static T ReadAbi<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidCastException or OverflowException)
        {
            throw InvalidAbi(exception);
        }
    }

    internal sealed record TerminalEvent(
        EntityId EventId,
        long SourceSequence,
        string EventType,
        EntityId GroupId,
        EntityId PeriodId,
        EntityId ReservationId,
        EntityId AttemptId,
        BigInteger DeltaConsumedTokens,
        long MatchingCount);

    internal sealed record AdjustmentEvent(
        EntityId EventId,
        long SourceSequence,
        string EventType,
        EntityId GroupId,
        EntityId PeriodId,
        EntityId ReservationId,
        EntityId AttemptId,
        BigInteger EventDeltaTokens,
        BigInteger FactDeltaTokens,
        long MatchingCount);
}
