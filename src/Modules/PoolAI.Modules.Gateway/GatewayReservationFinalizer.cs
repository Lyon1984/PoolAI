using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayReservationFinalizer(
    IGroupQuotaLedger quotaLedger,
    GatewayUpstreamAttemptOperation upstreamOperation,
    AccountLeaseLifetimeOperation accountLeaseOperation,
    TimeProvider timeProvider) : IReservationFinalizationPort
{
    private readonly IGroupQuotaLedger _quotaLedger = quotaLedger
        ?? throw new ArgumentNullException(nameof(quotaLedger));
    private readonly GatewayUpstreamAttemptOperation _upstreamOperation =
        upstreamOperation
        ?? throw new ArgumentNullException(nameof(upstreamOperation));
    private readonly AccountLeaseLifetimeOperation _accountLeaseOperation =
        accountLeaseOperation
        ?? throw new ArgumentNullException(nameof(accountLeaseOperation));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    internal ResultError? Failure { get; private set; }

    internal UsageAttemptOutcome AttemptOutcome { get; private set; }

    internal string? ErrorCode { get; private set; }

    public ValueTask SettleKnownUsageAsync(
        DispatchedReservationHandle reservation,
        ReservationSettlementEvidence.KnownUsage usage,
        ReservationLifetimeStopReason stopReason,
        CancellationToken cancellationToken) => SettleAsync(
        reservation,
        usage.Usage,
        usage.UsageSource,
        cancellationToken,
        stopReason);

    public ValueTask SettleConservativelyAsync(
        DispatchedReservationHandle reservation,
        ConservativeReservationSettlement settlement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        TokenEstimateSplit estimate = reservation.Estimate;
        return SettleAsync(
            reservation,
            new TokenUsage(
                new BigInteger(estimate.InputTokens),
                new BigInteger(estimate.OutputTokens),
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.ConservativeEstimate,
            cancellationToken,
            settlement.Reason);
    }

    private async ValueTask SettleAsync(
        DispatchedReservationHandle reservation,
        TokenUsage usage,
        SettlementUsageSource usageSource,
        CancellationToken cancellationToken,
        ReservationLifetimeStopReason stopReason =
            ReservationLifetimeStopReason.Completed)
    {
        NormalizedUpstreamResult? upstream = _upstreamOperation.UpstreamResult;
        AttemptOutcome = DetermineOutcome(upstream, stopReason);
        ErrorCode = DetermineErrorCode(
            upstream,
            stopReason,
            AttemptOutcome,
            usageSource);
        DateTimeOffset completedAt = CompletionTime(reservation);
        DateTimeOffset? firstTokenAt = ValidFirstTokenAt(
            reservation,
            upstream,
            completedAt);
        SettleReservationCommand command = new(
            reservation,
            AttemptOutcome,
            ValidStatusCode(upstream?.StatusCode),
            ErrorCode,
            BlankToNull(upstream?.UpstreamRequestId),
            firstTokenAt,
            completedAt,
            ToRequestOutcome(AttemptOutcome),
            usage,
            usageSource,
            usageSource == SettlementUsageSource.Upstream
                ? upstream?.Usage?.RawEvidence
                : null);

        try
        {
            Result<QuotaTransitionResult> settled = await _quotaLedger
                .SettleAsync(command, cancellationToken)
                .ConfigureAwait(false);
            if (settled.IsFailure)
            {
                Failure = settled.Error;
            }
        }
        catch (Exception)
        {
            Failure = new ResultError(
                ErrorCodesV1.DependencyUnavailable,
                "The dispatched reservation could not be finalized.",
                RetryAfterSeconds: 1);
        }
    }

    private DateTimeOffset CompletionTime(
        DispatchedReservationHandle reservation)
    {
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        if (completedAt < reservation.DispatchStartedAt)
        {
            completedAt = reservation.DispatchStartedAt;
        }

        return completedAt;
    }

    private static DateTimeOffset? ValidFirstTokenAt(
        DispatchedReservationHandle reservation,
        NormalizedUpstreamResult? upstream,
        DateTimeOffset completedAt) =>
        upstream?.FirstTokenAt is DateTimeOffset value
        && value >= reservation.DispatchStartedAt
        && value <= completedAt
            ? value
            : null;

    private static int? ValidStatusCode(int? value) =>
        value is >= 100 and <= 599 ? value : null;

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private UsageAttemptOutcome DetermineOutcome(
        NormalizedUpstreamResult? upstream,
        ReservationLifetimeStopReason stopReason)
    {
        if (stopReason == ReservationLifetimeStopReason.ClientDisconnected
            || _upstreamOperation.WasCancelled
                && _accountLeaseOperation.StopReason
                    == AccountLeaseLifetimeStopReason.Completed
                && stopReason is not (
                    ReservationLifetimeStopReason.Completed
                    or ReservationLifetimeStopReason.AttemptDeadlineReached))
        {
            return UsageAttemptOutcome.Cancelled;
        }

        return upstream is not null
            && _upstreamOperation.Failure is null
            && upstream.StatusCode is >= 200 and <= 299
            && string.IsNullOrWhiteSpace(upstream.ErrorCode)
            ? UsageAttemptOutcome.Succeeded
            : UsageAttemptOutcome.Failed;
    }

    private string? DetermineErrorCode(
        NormalizedUpstreamResult? upstream,
        ReservationLifetimeStopReason stopReason,
        UsageAttemptOutcome outcome,
        SettlementUsageSource usageSource)
    {
        if (stopReason is ReservationLifetimeStopReason.RenewalFailed
            or ReservationLifetimeStopReason.HardDeadlineReached)
        {
            return ErrorCodesV1.InternalError;
        }

        if (outcome == UsageAttemptOutcome.Succeeded)
        {
            return null;
        }

        if (string.Equals(
                _upstreamOperation.Failure?.Code,
                ErrorCodesV1.UpstreamProtocolError,
                StringComparison.Ordinal))
        {
            return ErrorCodesV1.UpstreamProtocolError;
        }

        if (usageSource == SettlementUsageSource.ConservativeEstimate
            && _upstreamOperation.Phase
                >= GatewayAttemptPhase.BusinessOutputStarted)
        {
            return ErrorCodesV1.UpstreamStreamError;
        }

        if (usageSource == SettlementUsageSource.ConservativeEstimate)
        {
            return ErrorCodesV1.UpstreamDispatchAmbiguous;
        }

        if (usageSource == SettlementUsageSource.ConfirmedNoExecution)
        {
            return BlankToNull(upstream?.ErrorCode)
                ?? ErrorCodesV1.UpstreamUnavailable;
        }

        return BlankToNull(upstream?.ErrorCode)
            ?? BlankToNull(_upstreamOperation.Failure?.Code)
            ?? ErrorCodesV1.UpstreamStreamError;
    }

    private static UsageRequestOutcome ToRequestOutcome(
        UsageAttemptOutcome outcome) => outcome switch
        {
            UsageAttemptOutcome.Succeeded => UsageRequestOutcome.Succeeded,
            UsageAttemptOutcome.Failed => UsageRequestOutcome.Failed,
            UsageAttemptOutcome.Cancelled => UsageRequestOutcome.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
