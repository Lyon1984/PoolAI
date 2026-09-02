using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Owns the live, in-process state and external-resource handles for exactly one
/// Gateway attempt. The Adapter receives only <see cref="AdapterContext"/>;
/// phase/evidence mutation remains inside the Gateway boundary.
/// </summary>
internal sealed class GatewayAttemptLifecycle :
    IGatewayAttemptOutputEvidenceSink
{
    private readonly Lock _gate = new();
    private ReservationHandle? _reservation;
    private DispatchedReservationHandle? _dispatchedReservation;
    private GatewayRequestWriteEvidence _requestWriteEvidence;
    private NormalizedUpstreamResult? _upstreamResult;
    private string? _transportErrorCode;
    private bool _confirmedNoExecution;
    private bool _transportObserved;
    private GatewaySingleAttemptDisposition? _finalDisposition;

    internal GatewayAttemptLifecycle(
        EntityId requestId,
        EntityId attemptId,
        int attemptIndex,
        EntityId quotaGroupId,
        EntityId routingGroupId,
        AdapterRouteSnapshot route,
        IAccountLease accountLease,
        DateTimeOffset deadline,
        int remainingRetryBudget)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(accountLease);
        if (quotaGroupId.Value.Version != 7
            || routingGroupId.Value.Version != 7
            || quotaGroupId != routingGroupId
            || route.GroupId != routingGroupId
            || accountLease.Route.GroupId != routingGroupId
            || accountLease.Route.ChannelId != route.ChannelId
            || accountLease.Route.AccountId != route.AccountId)
        {
            throw new ArgumentException(
                "The Gateway attempt ownership boundary is invalid.",
                nameof(quotaGroupId));
        }

        QuotaGroupId = quotaGroupId;
        RoutingGroupId = routingGroupId;
        AccountLease = accountLease;
        AdapterContext = new AdapterAttemptContext(
            requestId,
            attemptId,
            attemptIndex,
            route,
            deadline,
            remainingRetryBudget,
            this);
    }

    internal EntityId QuotaGroupId { get; }

    internal EntityId RoutingGroupId { get; }

    internal IAccountLease AccountLease { get; }

    internal AdapterAttemptContext AdapterContext { get; }

    internal GatewayAttemptPhase Phase => AdapterContext.Phase;

    internal ReservationHandle? Reservation
    {
        get
        {
            lock (_gate)
            {
                return _reservation;
            }
        }
    }

    internal GatewaySingleAttemptDisposition? FinalDisposition
    {
        get
        {
            lock (_gate)
            {
                return _finalDisposition;
            }
        }
    }

    internal GatewayAttemptEvidence Evidence
    {
        get
        {
            lock (_gate)
            {
                return new GatewayAttemptEvidence(
                    _requestWriteEvidence,
                    _upstreamResult,
                    _transportErrorCode,
                    _confirmedNoExecution);
            }
        }
    }

    internal void BindReservation(ReservationHandle reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            EnsureActive();
            if (_reservation is not null
                || reservation.RequestId != AdapterContext.RequestId
                || reservation.AttemptId != AdapterContext.AttemptId
                || reservation.AttemptIndex != AdapterContext.AttemptIndex
                || reservation.GroupId != QuotaGroupId
                || reservation.AccountId != AdapterContext.Route.AccountId
                || reservation.ChannelId != AdapterContext.Route.ChannelId)
            {
                throw new InvalidOperationException(
                    "The quota reservation is not owned by this Gateway attempt.");
            }

            _reservation = reservation;
        }
    }

    internal void MarkDispatchFenceCommitted(
        DispatchedReservationHandle dispatchedReservation)
    {
        ArgumentNullException.ThrowIfNull(dispatchedReservation);
        lock (_gate)
        {
            EnsureActive();
            if (_reservation is null
                || _dispatchedReservation is not null
                || dispatchedReservation.Reservation != _reservation
                || dispatchedReservation.Status != ReservationStatus.Pending)
            {
                throw new InvalidOperationException(
                    "The dispatched reservation is not owned by this Gateway attempt.");
            }

            AdapterContext.MarkDispatchedAfterFence();
            _dispatchedReservation = dispatchedReservation;
        }
    }

    internal void RecordTransportResult(GatewayUpstreamTransportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            EnsureActive();
            EnsureDispatched();
            if (_transportObserved)
            {
                throw new InvalidOperationException(
                    "Transport evidence has already been recorded for this attempt.");
            }

            _transportObserved = true;
            _requestWriteEvidence = result.WriteEvidence;
            if (result.RequestBytesWritten)
            {
                AdapterContext.MarkRequestBytesWritten();
            }

            if (result.Response.IsSuccess)
            {
                _upstreamResult = result.Response.Value;
            }
            else
            {
                _transportErrorCode = result.Response.Error.Code;
            }

            _confirmedNoExecution = result.ConfirmedNoExecution;
        }
    }

    internal void Complete(GatewaySingleAttemptDisposition disposition)
    {
        lock (_gate)
        {
            if (_finalDisposition is GatewaySingleAttemptDisposition existing)
            {
                if (existing != disposition)
                {
                    throw new InvalidOperationException(
                        "The Gateway attempt final disposition is immutable.");
                }

                return;
            }

            _finalDisposition = disposition;
        }
    }

    void IGatewayAttemptOutputEvidenceSink.MarkDownstreamHeadersCommitted()
    {
        lock (_gate)
        {
            EnsureActive();
            EnsureDispatched();
            AdapterContext.AdvanceToDownstreamHeadersCommitted();
        }
    }

    void IGatewayAttemptOutputEvidenceSink.MarkBusinessOutputStarted()
    {
        lock (_gate)
        {
            EnsureActive();
            EnsureDispatched();
            AdapterContext.AdvanceToBusinessOutputStarted();
        }
    }

    private void EnsureActive()
    {
        if (_finalDisposition is not null)
        {
            throw new InvalidOperationException(
                "The Gateway attempt is already terminal.");
        }
    }

    private void EnsureDispatched()
    {
        if (_dispatchedReservation is null)
        {
            throw new InvalidOperationException(
                "The dispatch fence has not committed for this Gateway attempt.");
        }
    }
}
