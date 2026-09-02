using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayUpstreamAttemptOperation(
    IPreparedUpstreamAttempt preparedAttempt,
    GatewayAttemptLifecycle attemptLifecycle,
    AdapterCapability capability,
    IUpstreamCredentialHandle credential,
    IGatewayUpstreamTransport transport) : IReservationLifetimeOperation
{
    private readonly IPreparedUpstreamAttempt _preparedAttempt =
        preparedAttempt ?? throw new ArgumentNullException(nameof(preparedAttempt));
    private readonly GatewayAttemptLifecycle _attemptLifecycle =
        attemptLifecycle
        ?? throw new ArgumentNullException(nameof(attemptLifecycle));
    private readonly AdapterCapability _capability = capability
        ?? throw new ArgumentNullException(nameof(capability));
    private readonly IUpstreamCredentialHandle _credential = credential
        ?? throw new ArgumentNullException(nameof(credential));
    private readonly IGatewayUpstreamTransport _transport = transport
        ?? throw new ArgumentNullException(nameof(transport));
    private int _executed;

    internal NormalizedUpstreamResult? UpstreamResult { get; private set; }

    internal ResultError? Failure { get; private set; }

    internal bool WasCancelled { get; private set; }

    internal GatewayAttemptPhase Phase => _attemptLifecycle.Phase;

    public ValueTask<ReservationSettlementEvidence> ExecuteAsync(
        ReservationLifetimeCancellation cancellation)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                "A prepared upstream attempt is single-use.");
        }

        if (_attemptLifecycle.Phase
            < GatewayAttemptPhase.DispatchedNoDownstreamHeaders)
        {
            throw new InvalidOperationException(
                "The dispatch fence must commit before upstream send.");
        }

        return SendAsync(cancellation);
    }

    private async ValueTask<ReservationSettlementEvidence> SendAsync(
        ReservationLifetimeCancellation cancellation)
    {
        try
        {
            GatewayUpstreamTransportResult sent = await _transport.SendAsync(
                    _preparedAttempt,
                    _attemptLifecycle.AdapterContext,
                    _capability,
                    _credential,
                    cancellation.AbortUpstream)
                .ConfigureAwait(false);
            _attemptLifecycle.RecordTransportResult(sent);

            if (sent.Response.IsFailure)
            {
                Failure = sent.Response.Error;
                WasCancelled = cancellation.AbortUpstream.IsCancellationRequested;
                return CanConfirmNoExecution(sent, result: null)
                    ? ConfirmedNoExecution()
                    : ReservationSettlementEvidence.NoKnownUsage.Instance;
            }

            return Normalize(sent.Response.Value, sent);
        }
        catch (OperationCanceledException)
            when (cancellation.AbortUpstream.IsCancellationRequested)
        {
            WasCancelled = true;
            return ReservationSettlementEvidence.NoKnownUsage.Instance;
        }
        catch (Exception)
        {
            Failure = new ResultError(
                ErrorCodesV1.UpstreamDispatchAmbiguous,
                "The dispatched upstream attempt failed without reliable usage evidence.");
            return ReservationSettlementEvidence.NoKnownUsage.Instance;
        }
    }

    private ReservationSettlementEvidence Normalize(
        NormalizedUpstreamResult result,
        GatewayUpstreamTransportResult transport)
    {
        UpstreamResult = result;
        if (!HasValidResultShape(result, transport))
        {
            Failure = new ResultError(
                ErrorCodesV1.UpstreamProtocolError,
                "The upstream result evidence is internally inconsistent.");
            return result.Usage is null
                ? ReservationSettlementEvidence.NoKnownUsage.Instance
                : new ReservationSettlementEvidence.KnownUsage(
                    ToTokenUsage(result.Usage),
                    SettlementUsageSource.Upstream);
        }

        if (result.Usage is not null)
        {
            return new ReservationSettlementEvidence.KnownUsage(
                ToTokenUsage(result.Usage),
                SettlementUsageSource.Upstream);
        }

        return CanConfirmNoExecution(transport, result)
            ? ConfirmedNoExecution()
            : ReservationSettlementEvidence.NoKnownUsage.Instance;
    }

    private static ReservationSettlementEvidence.KnownUsage
        ConfirmedNoExecution() =>
        new ReservationSettlementEvidence.KnownUsage(
            new TokenUsage(
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.ConfirmedNoExecution);

    private static bool HasValidResultShape(
        NormalizedUpstreamResult result,
        GatewayUpstreamTransportResult transport) =>
        result.StatusCode is >= 100 and <= 599
        && (result.ErrorCode is null
            || !string.IsNullOrWhiteSpace(result.ErrorCode))
        && (result.UpstreamRequestId is null
            || !string.IsNullOrWhiteSpace(result.UpstreamRequestId))
        && (!transport.ConfirmedNoExecution || result.Usage is null)
        && (!transport.ConfirmedNoExecution
            || result.StatusCode is not (>= 200 and <= 299))
        && (!transport.ConfirmedNoExecution
            || !transport.RequestBytesWritten
            || result.StatusCode is 401 or 403 or 429);

    private bool CanConfirmNoExecution(
        GatewayUpstreamTransportResult transport,
        NormalizedUpstreamResult? result) =>
        transport.ConfirmedNoExecution
        && ((!transport.RequestBytesWritten
                && _capability.CanProveNoRequestBytesWritten)
            || result is { StatusCode: int statusCode }
                && _capability.ConfirmsNoExecutionForStatus(statusCode));

    private static TokenUsage ToTokenUsage(NormalizedUpstreamUsage usage) => new(
        usage.InputTokens,
        usage.OutputTokens,
        usage.CacheReadTokens,
        usage.CacheCreationTokens,
        usage.ThinkingTokens);
}
