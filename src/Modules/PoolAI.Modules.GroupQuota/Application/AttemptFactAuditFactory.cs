using System.Globalization;
using System.Numerics;
using System.Text.Json;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

internal static class AttemptFactAuditFactory
{
    private const string TargetType = "usage_attempt";

    internal static AuditEntry Settled(
        SettleReservationWrite write,
        QuotaTransitionRow row)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(row);
        ReservationHandle reservation = write.Command.Reservation.Reservation;
        return Entry(
            write.Mutation,
            "group_quota.attempt_fact_settled",
            reservation.AttemptId,
            reservation.RequestId,
            JsonSerializer.SerializeToElement(Tokens(write.Command.Usage)),
            JsonSerializer.SerializeToElement(new
            {
                quota_event_id = write.Mutation.EventId.Value,
                group_id = reservation.GroupId.Value,
                period_id = row.PeriodId.Value,
                reservation_id = row.ReservationId.Value,
                attempt_id = reservation.AttemptId.Value,
                outcome = Outcome(write.Command.AttemptOutcome),
                usage_source = Source(write.Command.UsageSource),
            }));
    }

    internal static AuditEntry UsageAdjusted(
        AdjustAttemptUsageWrite write,
        UsageAdjustmentRow row)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(row);
        return Entry(
            write.Mutation,
            "group_quota.attempt_fact_usage_adjusted",
            write.Command.AttemptId,
            requestId: null,
            JsonSerializer.SerializeToElement(new
            {
                previous_total_tokens = Canonical(row.PreviousTokens),
                corrected_total_tokens = Canonical(row.CorrectedTokens),
                delta_tokens = Canonical(row.DeltaTokens),
            }),
            JsonSerializer.SerializeToElement(new
            {
                quota_event_id = write.Mutation.EventId.Value,
                group_id = write.Command.GroupId.Value,
                period_id = row.PeriodId.Value,
                reservation_id = row.ReservationId.Value,
                attempt_id = write.Command.AttemptId.Value,
                outcome = Outcome(write.Command.AttemptOutcome),
                usage_source = Source(write.Command.UsageSource),
                token_counts = Tokens(write.Command.CorrectedUsage),
            }));
    }

    internal static AuditEntry ConservativeExpired(
        ExpireReservationWrite write,
        AttemptSettlementFact fact)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(fact);
        return Entry(
            write.Mutation,
            "group_quota.attempt_fact_conservative_expired",
            fact.AttemptId,
            fact.RequestId,
            JsonSerializer.SerializeToElement(Tokens(fact.Usage.Tokens)),
            JsonSerializer.SerializeToElement(new
            {
                quota_event_id = write.Mutation.EventId.Value,
                group_id = fact.GroupId.Value,
                period_id = fact.PeriodId.Value,
                reservation_id = fact.ReservationId.Value,
                attempt_id = fact.AttemptId.Value,
                outcome = Outcome(fact.Outcome),
                usage_source = Source(fact.Usage.Source),
            }));
    }

    private static AuditEntry Entry(
        QuotaMutationIdentity mutation,
        string action,
        EntityId attemptId,
        EntityId? requestId,
        JsonElement afterState,
        JsonElement metadata) => new(
            QuotaMutationIdentityFactory.AuditId(mutation),
            AuditActorType.Service,
            ActorUserId: null,
            action,
            TargetType,
            attemptId,
            requestId,
            Reason: null,
            IpAddress: null,
            UserAgent: null,
            BeforeState: null,
            afterState,
            metadata);

    private static object Tokens(TokenUsage usage) => new
    {
        input_tokens = Canonical(usage.InputTokens),
        output_tokens = Canonical(usage.OutputTokens),
        cache_read_tokens = Canonical(usage.CacheReadTokens),
        cache_creation_tokens = Canonical(usage.CacheCreationTokens),
        thinking_tokens = Canonical(usage.ThinkingTokens),
        total_tokens = Canonical(usage.TotalTokens),
    };

    private static string Canonical(BigInteger value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Outcome(UsageAttemptOutcome value) => value switch
    {
        UsageAttemptOutcome.Succeeded => "succeeded",
        UsageAttemptOutcome.Failed => "failed",
        UsageAttemptOutcome.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string Source(SettlementUsageSource value) => value switch
    {
        SettlementUsageSource.Upstream => "upstream",
        SettlementUsageSource.LocalTokenizer => "local_tokenizer",
        SettlementUsageSource.ConservativeEstimate => "conservative_estimate",
        SettlementUsageSource.ConfirmedNoExecution => "confirmed_no_execution",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
