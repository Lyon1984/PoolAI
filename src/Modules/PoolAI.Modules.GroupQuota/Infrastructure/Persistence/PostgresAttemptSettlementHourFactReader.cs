using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresAttemptSettlementHourFactReader :
    IAttemptSettlementHourFactReader
{
    private const string ReadHourSql = """
        WITH target AS MATERIALIZED (
            SELECT
                date_trunc(
                    'hour',
                    target_attempt.completed_at AT TIME ZONE 'UTC'
                ) AT TIME ZONE 'UTC' AS bucket_start
            FROM public.usage_attempts AS target_attempt
            JOIN public.group_token_reservations AS target_reservation
              ON target_reservation.id = target_attempt.reservation_id
             AND target_reservation.attempt_id = target_attempt.attempt_id
             AND target_reservation.group_id = target_attempt.quota_group_id
            WHERE target_attempt.attempt_id = $3
              AND target_attempt.quota_group_id = $1
              AND target_reservation.period_id = $2
        )
        SELECT
            target.bucket_start,
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
            adjustment.quota_event_id,
            adjustment.previous_total_tokens,
            adjustment.corrected_input_tokens,
            adjustment.corrected_output_tokens,
            adjustment.corrected_cache_read_tokens,
            adjustment.corrected_cache_creation_tokens,
            adjustment.corrected_thinking_tokens,
            adjustment.usage_source,
            adjustment.delta_tokens,
            adjustment.adjusted_at,
            usage_request.requested_model,
            usage_request.is_streaming,
            attempt.upstream_http_status,
            attempt.error_code,
            reservation.is_streaming
        FROM target
        JOIN public.usage_attempts AS attempt
          ON attempt.quota_group_id = $1
         AND attempt.completed_at >= target.bucket_start
         AND attempt.completed_at < target.bucket_start + interval '1 hour'
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
        LEFT JOIN public.usage_attempt_adjustments AS adjustment
          ON adjustment.attempt_id = attempt.attempt_id
        ORDER BY attempt.attempt_id;
        """;

    public async ValueTask<AttemptSettlementHourSnapshot?> ReadForAttemptAsync(
        EntityId groupId,
        EntityId periodId,
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWorkContext);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReadHourSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(periodId.Value);
        command.Parameters.AddWithValue(attemptId.Value);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        DateTimeOffset bucketStart = reader.GetFieldValue<DateTimeOffset>(0);
        List<AttemptSettlementFact> facts = [];
        bool containsTarget = false;
        do
        {
            AttemptSettlementFact fact = PostgresQuotaLedgerAbiContract
                .ReadAttemptFact(reader, offset: 1);
            if (fact.AttemptId == attemptId)
            {
                if (containsTarget)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL completion-hour fact snapshot duplicated its target.");
                }

                containsTarget = true;
            }

            facts.Add(fact);
        }
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        if (!containsTarget)
        {
            throw new InvalidOperationException(
                "The PostgreSQL completion-hour fact snapshot omitted its target.");
        }

        return new AttemptSettlementHourSnapshot(
            groupId,
            periodId,
            bucketStart,
            facts);
    }
}
