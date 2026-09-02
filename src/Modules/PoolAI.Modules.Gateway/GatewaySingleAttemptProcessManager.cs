using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Executes exactly one M4-E1 model attempt. It intentionally owns neither an
/// HTTP endpoint nor a failover loop.
/// </summary>
internal sealed class GatewaySingleAttemptProcessManager
{
    private static readonly TimeSpan MaximumTimerDelay =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1u);
    private readonly ConservativeTokenEstimator _estimator;
    private readonly IAccountRouter _accountRouter;
    private readonly IGroupQuotaLedger _quotaLedger;
    private readonly GatewayCredentialHandoff _credentialHandoff;
    private readonly IGatewayUpstreamTransport _upstreamTransport;
    private readonly IReadOnlyList<IUpstreamAdapter> _adapters;
    private readonly AdapterCapabilityRegistry _capabilityRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ReservationLifetimeCoordinator _reservationLifetime;

    internal GatewaySingleAttemptProcessManager(
        ConservativeTokenEstimator estimator,
        IAccountRouter accountRouter,
        IGroupQuotaLedger quotaLedger,
        GatewayCredentialHandoff credentialHandoff,
        IGatewayUpstreamTransport upstreamTransport,
        IEnumerable<IUpstreamAdapter> adapters,
        AdapterCapabilityRegistry capabilityRegistry,
        TimeProvider timeProvider,
        ReservationLifetimeCoordinator reservationLifetime)
    {
        _estimator = estimator
            ?? throw new ArgumentNullException(nameof(estimator));
        _accountRouter = accountRouter
            ?? throw new ArgumentNullException(nameof(accountRouter));
        _quotaLedger = quotaLedger
            ?? throw new ArgumentNullException(nameof(quotaLedger));
        _credentialHandoff = credentialHandoff
            ?? throw new ArgumentNullException(nameof(credentialHandoff));
        _upstreamTransport = upstreamTransport
            ?? throw new ArgumentNullException(nameof(upstreamTransport));
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = adapters.ToArray();
        _capabilityRegistry = capabilityRegistry
            ?? throw new ArgumentNullException(nameof(capabilityRegistry));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        _reservationLifetime = reservationLifetime
            ?? throw new ArgumentNullException(nameof(reservationLifetime));
    }

    internal ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
        GatewaySingleAttemptRequest command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new AttemptExecution(this, command, cancellationToken).RunAsync();
    }

    private sealed class AttemptExecution(
        GatewaySingleAttemptProcessManager owner,
        GatewaySingleAttemptRequest command,
        CancellationToken cancellationToken)
    {
        private readonly GatewaySingleAttemptProcessManager _owner = owner;
        private readonly GatewaySingleAttemptRequest _command = command;
        private readonly CancellationToken _callerCancellationToken =
            cancellationToken;
        private CancellationToken _attemptCancellationToken =
            cancellationToken;
        private EntityId _attemptId;
        private IAccountLease? _unboundAccountLease;
        private ReservationHandle? _unboundReservation;
        private GatewayAttemptLifecycle? _attempt;
        private IUpstreamCredentialHandle? _credential;
        private IPreparedUpstreamAttempt? _prepared;
        private bool _dispatchCommitted;
        private bool _preDispatchReleaseAttempted;

        internal async ValueTask<Result<GatewaySingleAttemptOutcome>> RunAsync()
        {
            DateTimeOffset startedAt = _owner._timeProvider.GetUtcNow();
            Result<bool> validation = Validate(
                _command,
                startedAt);
            if (validation.IsFailure)
            {
                return CopyFailure<GatewaySingleAttemptOutcome>(validation.Error);
            }

            TimeSpan remaining = _command.Deadline - startedAt;
            using CancellationTokenSource deadlineCancellation = new(
                remaining <= MaximumTimerDelay
                    ? remaining
                    : MaximumTimerDelay,
                _owner._timeProvider);
            using CancellationTokenSource attemptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _callerCancellationToken,
                    deadlineCancellation.Token);
            _attemptCancellationToken = attemptCancellation.Token;

            return await RunCoreAsync().ConfigureAwait(false);
        }

        private async ValueTask<Result<GatewaySingleAttemptOutcome>> RunCoreAsync()
        {
            try
            {
                Result<AdmissionState> admitted = BuildAdmissionState();
                if (admitted.IsFailure)
                {
                    return CopyFailure<GatewaySingleAttemptOutcome>(admitted.Error);
                }

                _attemptId = EntityId.New();
                Result<IAccountLease> routed = await RouteAsync(admitted.Value)
                    .ConfigureAwait(false);
                if (routed.IsFailure)
                {
                    return CopyFailure<GatewaySingleAttemptOutcome>(routed.Error);
                }

                _unboundAccountLease = routed.Value;
                Result<GatewaySingleAttemptOutcome> result =
                    await ExecuteRouteAsync(admitted.Value)
                    .ConfigureAwait(false);
                if (result.IsFailure)
                {
                    _attempt?.Complete(GatewaySingleAttemptDisposition.Failed);
                }

                return result;
            }
            catch (OperationCanceledException)
                when (_callerCancellationToken.IsCancellationRequested)
            {
                await ReleaseOnCancellationAsync().ConfigureAwait(false);
                _attempt?.Complete(GatewaySingleAttemptDisposition.Cancelled);
                throw;
            }
            catch (OperationCanceledException)
                when (_attemptCancellationToken.IsCancellationRequested
                    || AttemptDeadlineExpired())
            {
                await ReleaseOnCancellationAsync().ConfigureAwait(false);
                _attempt?.Complete(GatewaySingleAttemptDisposition.Cancelled);
                return Result.Failure<GatewaySingleAttemptOutcome>(
                    ErrorCodesV1.UpstreamUnavailable,
                    "The Gateway attempt deadline expired before completion.",
                    retryAfterSeconds: 1);
            }
            catch (Exception)
            {
                Result<GatewaySingleAttemptOutcome> result =
                    await HandleUnexpectedFailureAsync().ConfigureAwait(false);
                _attempt?.Complete(GatewaySingleAttemptDisposition.Failed);
                return result;
            }
            finally
            {
                await CleanupAsync().ConfigureAwait(false);
            }
        }

        private Result<AdmissionState> BuildAdmissionState()
        {
            EnsureAttemptActive();
            Result<GatewayTokenEstimate> estimate = _owner._estimator.Estimate(
                _command.Protocol,
                _command.Request.Payload);
            return estimate.IsFailure
                ? CopyFailure<AdmissionState>(estimate.Error)
                : Result.Success(new AdmissionState(
                    _command.Access,
                    estimate.Value));
        }

        private ValueTask<Result<IAccountLease>> RouteAsync(
            AdmissionState admitted)
        {
            EnsureAttemptActive();
            return _owner._accountRouter.RouteAsync(
                new RouteAccountCommand(
                    admitted.Canonical.Group.GroupId,
                    _command.Request.Model,
                    _command.Request.RequestId,
                    _attemptId,
                    admitted.Canonical.Group.Version,
                    _command.SessionAffinityHash),
                _attemptCancellationToken);
        }

        private async ValueTask<Result<GatewaySingleAttemptOutcome>>
            ExecuteRouteAsync(AdmissionState admitted)
        {
            AccountRoute route = _unboundAccountLease!.Route;
            if (!IsCompatibleRoute(
                    route,
                    admitted.Canonical,
                    _command,
                    _owner._timeProvider.GetUtcNow()))
            {
                return DependencyUnavailable<GatewaySingleAttemptOutcome>(
                    "The selected Account route is inconsistent.");
            }

            _attempt = CreateAttemptLifecycle(admitted.Canonical, route);
            _unboundAccountLease = null;

            Result<ResolvedAdapter> adapter = _owner.ResolveAdapter(
                _command.Protocol,
                _command.Request.Stream,
                route);
            if (adapter.IsFailure)
            {
                return CopyFailure<GatewaySingleAttemptOutcome>(adapter.Error);
            }

            Result<ReserveQuotaResult> reserved = await ReserveAsync(
                    admitted,
                    route)
                .ConfigureAwait(false);
            if (reserved.IsFailure)
            {
                return CopyFailure<GatewaySingleAttemptOutcome>(reserved.Error);
            }

            _unboundReservation = reserved.Value.Reservation;
            if (!IsCompatibleReservation(
                    _unboundReservation,
                    _command,
                    _attemptId,
                    route,
                    admitted.Estimate))
            {
                return await FailBeforeDispatchAsync(new ResultError(
                        ErrorCodesV1.DependencyUnavailable,
                        "The quota reservation result is inconsistent.",
                        RetryAfterSeconds: 1))
                    .ConfigureAwait(false);
            }

            _attempt.BindReservation(_unboundReservation);
            _unboundReservation = null;

            return await ExecuteReservedAsync(admitted.Estimate, route, adapter.Value)
                .ConfigureAwait(false);
        }

        private ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
            AdmissionState admitted,
            AccountRoute route)
        {
            EnsureAttemptActive();
            return _owner._quotaLedger.ReserveAsync(
                new ReserveQuotaCommand(
                    _command.Request.RequestId,
                    _attemptId,
                    _command.AttemptIndex,
                    admitted.Canonical.User.UserId,
                    admitted.Canonical.ApiKey.ApiKeyId,
                    admitted.Canonical.Subscription.SubscriptionId,
                    admitted.Canonical.Group.GroupId,
                    route.AccountId,
                    route.ChannelId,
                    ToEndpoint(_command.Protocol),
                    _command.Request.Model,
                    _command.ClientRequestId,
                    admitted.Estimate.ToReservationTokenCount(),
                    _command.Request.Stream,
                    _command.LeaseOwner),
                _attemptCancellationToken);
        }

        private async ValueTask<Result<GatewaySingleAttemptOutcome>>
            ExecuteReservedAsync(
                GatewayTokenEstimate estimate,
                AccountRoute route,
                ResolvedAdapter resolvedAdapter)
        {
            EnsureAttemptActive();
            Result<IUpstreamCredentialHandle> acquired = await _owner
                ._credentialHandoff.AcquireAsync(
                    route,
                    _attemptCancellationToken)
                .ConfigureAwait(false);
            if (acquired.IsFailure)
            {
                return await FailBeforeDispatchAsync(acquired.Error)
                    .ConfigureAwait(false);
            }

            _credential = acquired.Value;
            EnsureAttemptActive();
            Result<IPreparedUpstreamAttempt> preparation = await resolvedAdapter
                .Adapter
                .PrepareAsync(
                    _attempt!.AdapterContext,
                    _command.Request,
                    _attemptCancellationToken)
                .ConfigureAwait(false);
            if (preparation.IsFailure)
            {
                return await FailBeforeDispatchAsync(preparation.Error)
                    .ConfigureAwait(false);
            }

            _prepared = preparation.Value;
            Result<AccountRoute> renewed = await RenewBeforeDispatchAsync(route)
                .ConfigureAwait(false);
            if (renewed.IsFailure)
            {
                return await FailBeforeDispatchAsync(renewed.Error)
                    .ConfigureAwait(false);
            }

            route = renewed.Value;
            Result<DispatchedReservationHandle> dispatched =
                await MarkDispatchedAsync(estimate, route).ConfigureAwait(false);
            if (dispatched.IsFailure)
            {
                return await FailBeforeDispatchAsync(dispatched.Error)
                    .ConfigureAwait(false);
            }

            _dispatchCommitted = true;
            _attempt.MarkDispatchFenceCommitted(dispatched.Value);
            return await ExecuteDispatchedAsync(
                    dispatched.Value,
                    resolvedAdapter.Capability)
                .ConfigureAwait(false);
        }

        private GatewayAttemptLifecycle CreateAttemptLifecycle(
            GatewayCanonicalAccess canonical,
            AccountRoute route) => new(
                _command.Request.RequestId,
                _attemptId,
                _command.AttemptIndex,
                canonical.Group.GroupId,
                route.GroupId,
                ToAdapterRoute(route),
                _unboundAccountLease!,
                _command.Deadline,
                _command.RemainingRetryBudget);

        private ValueTask<Result<DispatchedReservationHandle>>
            MarkDispatchedAsync(
                GatewayTokenEstimate estimate,
                AccountRoute route)
        {
            EnsureAttemptActive();
            return _owner._quotaLedger.MarkDispatchedAsync(
                new MarkReservationDispatchedCommand(
                    _attempt!.Reservation!,
                    ToSettlementProvider(route.Provider),
                    route.UpstreamModel,
                    new TokenEstimateSplit(
                        checked((long)estimate.InputTokens),
                        checked((long)estimate.OutputTokens))),
                _attemptCancellationToken);
        }

        private async ValueTask<Result<GatewaySingleAttemptOutcome>>
            ExecuteDispatchedAsync(
                DispatchedReservationHandle dispatched,
                AdapterCapability capability)
        {
            GatewayUpstreamAttemptOperation upstream = new(
                _prepared!,
                _attempt!,
                capability,
                _credential!,
                _owner._upstreamTransport);
            AccountLeaseLifetimeOperation accountLifetime = new(
                _attempt!.AccountLease,
                upstream,
                _owner._timeProvider,
                _owner._reservationLifetime.DrainDuration);
            GatewayReservationFinalizer finalizer = new(
                _owner._quotaLedger,
                upstream,
                accountLifetime,
                _owner._timeProvider);
            ReservationLifetimeResult lifetime = await _owner
                ._reservationLifetime.ExecuteAsync(
                    dispatched,
                    accountLifetime,
                    finalizer,
                    _command.Deadline,
                    _callerCancellationToken)
                .ConfigureAwait(false);
            if (finalizer.Failure is not null)
            {
                _attempt.Complete(GatewaySingleAttemptDisposition.Failed);
                return DispatchedFailure(finalizer.Failure);
            }

            GatewaySingleAttemptDisposition disposition = ToDisposition(
                finalizer.AttemptOutcome);
            _attempt.Complete(disposition);
            GatewayAttemptEvidence evidence = _attempt.Evidence;
            return Result.Success(new GatewaySingleAttemptOutcome(
                _command.Request.RequestId,
                _attemptId,
                _command.AttemptIndex,
                _attempt.Phase,
                disposition,
                evidence.UpstreamResult,
                lifetime,
                accountLifetime.StopReason,
                finalizer.ErrorCode));
        }

        private async ValueTask<Result<GatewaySingleAttemptOutcome>>
            FailBeforeDispatchAsync(ResultError failure)
        {
            _preDispatchReleaseAttempted = true;
            return await _owner.ReleaseBeforeDispatchAsync(
                    OwnedReservation!,
                    failure)
                .ConfigureAwait(false);
        }

        private async ValueTask ReleaseOnCancellationAsync()
        {
            if (OwnedReservation is null || _dispatchCommitted)
            {
                return;
            }

            _ = await FailBeforeDispatchAsync(new ResultError(
                    ErrorCodesV1.InvalidRequest,
                    "The request was cancelled before dispatch."))
                .ConfigureAwait(false);
        }

        private ValueTask<Result<GatewaySingleAttemptOutcome>>
            HandleUnexpectedFailureAsync()
        {
            if (_dispatchCommitted)
            {
                return ValueTask.FromResult(DispatchedFailure());
            }

            ResultError failure = new(
                ErrorCodesV1.DependencyUnavailable,
                "The Gateway attempt could not be completed safely.",
                RetryAfterSeconds: 1);
            return OwnedReservation is not null
                ? FailBeforeDispatchAsync(failure)
                : ValueTask.FromResult(
                    CopyFailure<GatewaySingleAttemptOutcome>(failure));
        }

        private async ValueTask CleanupAsync()
        {
            await ReleaseForgottenReservationAsync().ConfigureAwait(false);

            if (_prepared is not null)
            {
                await DisposePreparedAsync(_prepared).ConfigureAwait(false);
            }

            try
            {
                _credential?.Dispose();
            }
            catch (Exception)
            {
                // The one-use lease has no safe recovery operation here.
            }

            IAccountLease? accountLease = _attempt?.AccountLease
                ?? _unboundAccountLease;
            if (accountLease is not null)
            {
                await ReleaseAccountLeaseAsync(accountLease).ConfigureAwait(false);
            }
        }

        private async ValueTask<Result<AccountRoute>> RenewBeforeDispatchAsync(
            AccountRoute route)
        {
            EnsureAttemptActive();
            AccountLeaseRenewResult renewed;
            try
            {
                renewed = await _attempt!.AccountLease
                    .RenewAsync(_attemptCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (_attemptCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return CoordinationUnavailable<AccountRoute>();
            }

            if (renewed.Disposition
                == AccountLeaseRenewDisposition.CoordinationUnavailable)
            {
                return CoordinationUnavailable<AccountRoute>();
            }

            return renewed.Disposition == AccountLeaseRenewDisposition.Renewed
                && renewed.Route is { } renewedRoute
                && IsCompatibleRenewedRoute(
                    route,
                    renewedRoute,
                    _owner._timeProvider.GetUtcNow())
                && renewedRoute == _attempt.AccountLease.Route
                    ? Result.Success(renewedRoute)
                    : Result.Failure<AccountRoute>(
                        ErrorCodesV1.AccountCapacityUnavailable,
                        "The selected Account lease was lost before dispatch.",
                        retryAfterSeconds: 1);
        }

        private void EnsureAttemptActive()
        {
            _attemptCancellationToken.ThrowIfCancellationRequested();
            if (AttemptDeadlineExpired())
            {
                throw new OperationCanceledException(
                    "The Gateway attempt deadline expired.",
                    innerException: null,
                    _attemptCancellationToken);
            }
        }

        private bool AttemptDeadlineExpired() =>
            _owner._timeProvider.GetUtcNow() >= _command.Deadline;

        private async ValueTask ReleaseForgottenReservationAsync()
        {
            if (OwnedReservation is null
                || _dispatchCommitted
                || _preDispatchReleaseAttempted)
            {
                return;
            }

            _ = await FailBeforeDispatchAsync(new ResultError(
                    ErrorCodesV1.DependencyUnavailable,
                    "The attempt ended before dispatch.",
                    RetryAfterSeconds: 1))
                .ConfigureAwait(false);
        }

        private ReservationHandle? OwnedReservation =>
            _attempt?.Reservation ?? _unboundReservation;

        private sealed record AdmissionState(
            GatewayCanonicalAccess Canonical,
            GatewayTokenEstimate Estimate);

        private static Result<GatewaySingleAttemptOutcome> DispatchedFailure(
            ResultError? settlementFailure = null) =>
            string.Equals(
                settlementFailure?.Code,
                ErrorCodesV1.TokenNumericOverflow,
                StringComparison.Ordinal)
                ? Result.Failure<GatewaySingleAttemptOutcome>(
                    ErrorCodesV1.TokenNumericOverflow,
                    "The exact Token count exceeds the supported range.")
                : Result.Failure<GatewaySingleAttemptOutcome>(
                    ErrorCodesV1.UpstreamDispatchAmbiguous,
                    "The dispatched upstream attempt has uncertain settlement state.");
    }

    private Result<ResolvedAdapter> ResolveAdapter(
        InboundProtocol protocol,
        bool stream,
        AccountRoute route)
    {
        UpstreamType upstream = ToUpstreamType(route.Provider);
        AdapterOperation operation = stream
            ? AdapterOperation.Stream
            : AdapterOperation.NonStream;
        AdapterCapability expected;
        try
        {
            expected = _capabilityRegistry.Get(protocol, upstream, operation);
        }
        catch (KeyNotFoundException)
        {
            return DependencyUnavailable<ResolvedAdapter>(
                "The compatible upstream Adapter capability is not registered.");
        }

        IUpstreamAdapter[] matches = _adapters
            .Where(candidate => candidate is not null
                && candidate.Capability == expected)
            .ToArray();
        return matches.Length == 1
            ? Result.Success(new ResolvedAdapter(matches[0], expected))
            : DependencyUnavailable<ResolvedAdapter>(
                "Exactly one compatible upstream Adapter must be registered.");
    }

    private sealed record ResolvedAdapter(
        IUpstreamAdapter Adapter,
        AdapterCapability Capability);

    private async ValueTask<Result<GatewaySingleAttemptOutcome>>
        ReleaseBeforeDispatchAsync(
            ReservationHandle reservation,
            ResultError originalFailure)
    {
        try
        {
            Result<QuotaTransitionResult> released = await _quotaLedger
                .ReleaseAsync(
                    new ReleaseReservationCommand(
                        reservation,
                        "gateway_pre_dispatch_failure"),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return released.IsFailure
                ? CopyFailure<GatewaySingleAttemptOutcome>(released.Error)
                : CopyFailure<GatewaySingleAttemptOutcome>(originalFailure);
        }
        catch (Exception)
        {
            return DependencyUnavailable<GatewaySingleAttemptOutcome>(
                "The pre-dispatch reservation could not be released.");
        }
    }

    private static async ValueTask ReleaseAccountLeaseAsync(
        IAccountLease accountLease)
    {
        try
        {
            _ = await accountLease.ReleaseAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The Redis lease has a bounded TTL. Cleanup cannot replace the
            // already determined pre-dispatch result.
        }

        try
        {
            await accountLease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Disposal is best-effort after explicit release.
        }
    }

    private static async ValueTask DisposePreparedAsync(
        IPreparedUpstreamAttempt prepared)
    {
        try
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Dispatch/settlement evidence must not be replaced by transport
            // disposal after the attempt has already reached a terminal path.
        }
    }

    private static Result<bool> Validate(
        GatewaySingleAttemptRequest command,
        DateTimeOffset now)
    {
        NormalizedGatewayRequest? request = command.Request;
        bool valid = command.Protocol is InboundProtocol.Responses
                or InboundProtocol.ChatCompletions
            && IsValidCanonicalAccess(command.Access)
            && request is not null
            && request.RequestId.Value.Version == 7
            && request.Model is { Length: >= 1 and <= 200 }
            && string.Equals(
                request.Model,
                request.Model.Trim(),
                StringComparison.Ordinal)
            && request.Payload.ValueKind == JsonValueKind.Object
            && command.AttemptIndex >= 0
            && (command.ClientRequestId is null
                || !string.IsNullOrWhiteSpace(command.ClientRequestId))
            && !string.IsNullOrWhiteSpace(command.LeaseOwner)
            && command.Deadline > now
            && command.RemainingRetryBudget >= 0
            && (command.SessionAffinityHash is null
                || !string.IsNullOrWhiteSpace(command.SessionAffinityHash));
        return valid
            ? Result.Success(true)
            : Result.Failure<bool>(
                ErrorCodesV1.InvalidRequest,
                "The single-attempt Gateway command is invalid.");
    }

    private static bool IsValidCanonicalAccess(GatewayCanonicalAccess? access) =>
        access is not null
        && access.ApiKey.IsEffective
        && access.ApiKey.ApiKeyId.Value.Version == 7
        && access.User.UserId.Value.Version == 7
        && access.Subscription.SubscriptionId.Value.Version == 7
        && access.Group.GroupId.Value.Version == 7
        && access.ApiKey.UserId == access.User.UserId
        && access.ApiKey.GroupId == access.Group.GroupId
        && access.Subscription.UserId == access.User.UserId
        && access.Subscription.GroupId == access.Group.GroupId
        && access.User.Lifecycle
            == PoolAI.Modules.Identity.Abstractions.UserLifecycle.Active
        && access.Subscription.EffectiveStatus
            == PoolAI.Modules.SubscriptionAccess.Abstractions
                .SubscriptionEffectiveStatus.Active
        && access.Group.Lifecycle == GroupLifecycle.Active
        && access.Group.HasCurrentQuotaPeriod
        && access.ApiKey.Version > 0
        && access.User.Version > 0
        && access.Subscription.Version > 0
        && access.Group.Version > 0
        && access.Group.RequestsPerMinute is >= 1 and <= 1_000_000;

    private static bool IsCompatibleRoute(
        AccountRoute route,
        GatewayCanonicalAccess canonical,
        GatewaySingleAttemptRequest command,
        DateTimeOffset now) =>
        route.GroupId == canonical.Group.GroupId
        && route.ChannelId.Value.Version == 7
        && route.AccountId.Value.Version == 7
        && string.Equals(
            route.ClientModel,
            command.Request.Model,
            StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(route.UpstreamModel)
        && route.UpstreamBaseUri is { IsAbsoluteUri: true }
        && route.LeaseExpiresAt > now
        && route.SupplyConfigurationVersion > 0
        && route.ChannelVersion > 0
        && route.AccountVersion > 0
        && route.CredentialRevision > 0
        && (command.Protocol != InboundProtocol.Responses
            || route.Capabilities.Responses)
        && (command.Protocol != InboundProtocol.ChatCompletions
            || route.Capabilities.ChatCompletions)
        && (!command.Request.Stream || route.Capabilities.Streaming);

    private static bool IsCompatibleReservation(
        ReservationHandle reservation,
        GatewaySingleAttemptRequest command,
        EntityId attemptId,
        AccountRoute route,
        GatewayTokenEstimate estimate) =>
        reservation.RequestId == command.Request.RequestId
        && reservation.AttemptId == attemptId
        && reservation.AttemptIndex == command.AttemptIndex
        && reservation.GroupId == route.GroupId
        && reservation.AccountId == route.AccountId
        && reservation.ChannelId == route.ChannelId
        && reservation.EstimatedTokens == estimate.ToReservationTokenCount()
        && reservation.IsStreaming == command.Request.Stream
        && string.Equals(
            reservation.LeaseOwner,
            command.LeaseOwner,
            StringComparison.Ordinal)
        && reservation.LeaseExpiresAt <= reservation.MaxExpiresAt;

    private static bool IsCompatibleRenewedRoute(
        AccountRoute expected,
        AccountRoute renewed,
        DateTimeOffset now) =>
        renewed.GroupId == expected.GroupId
        && renewed.ChannelId == expected.ChannelId
        && renewed.AccountId == expected.AccountId
        && renewed.Provider == expected.Provider
        && string.Equals(
            renewed.ClientModel,
            expected.ClientModel,
            StringComparison.Ordinal)
        && string.Equals(
            renewed.UpstreamModel,
            expected.UpstreamModel,
            StringComparison.Ordinal)
        && renewed.UpstreamBaseUri == expected.UpstreamBaseUri
        && renewed.Capabilities == expected.Capabilities
        && renewed.SupplyConfigurationVersion
            == expected.SupplyConfigurationVersion
        && renewed.ChannelVersion == expected.ChannelVersion
        && renewed.AccountVersion == expected.AccountVersion
        && renewed.CredentialRevision == expected.CredentialRevision
        && renewed.LeaseExpiresAt > now;

    private static AdapterRouteSnapshot ToAdapterRoute(AccountRoute route) => new(
        route.GroupId,
        route.ChannelId,
        route.AccountId,
        ToUpstreamType(route.Provider),
        route.ClientModel,
        route.UpstreamModel,
        route.UpstreamBaseUri,
        route.Capabilities.Responses,
        route.Capabilities.ChatCompletions,
        route.Capabilities.FunctionTools,
        route.Capabilities.Streaming,
        route.SupplyConfigurationVersion,
        route.ChannelVersion,
        route.AccountVersion,
        route.CredentialRevision);

    private static UsageRequestEndpoint ToEndpoint(InboundProtocol protocol) =>
        protocol switch
        {
            InboundProtocol.Responses => UsageRequestEndpoint.Responses,
            InboundProtocol.ChatCompletions =>
                UsageRequestEndpoint.ChatCompletions,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };

    private static UpstreamType ToUpstreamType(AccountRouteProvider provider) =>
        provider switch
        {
            AccountRouteProvider.OpenAi => UpstreamType.OpenAi,
            AccountRouteProvider.OpenAiCompatible =>
                UpstreamType.OpenAiCompatible,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static SettlementProvider ToSettlementProvider(
        AccountRouteProvider provider) => provider switch
        {
            AccountRouteProvider.OpenAi => SettlementProvider.OpenAi,
            AccountRouteProvider.OpenAiCompatible =>
                SettlementProvider.OpenAiCompatible,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static GatewaySingleAttemptDisposition ToDisposition(
        UsageAttemptOutcome outcome) => outcome switch
        {
            UsageAttemptOutcome.Succeeded =>
                GatewaySingleAttemptDisposition.Succeeded,
            UsageAttemptOutcome.Failed =>
                GatewaySingleAttemptDisposition.Failed,
            UsageAttemptOutcome.Cancelled =>
                GatewaySingleAttemptDisposition.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };

    private static Result<T> DependencyUnavailable<T>(string description) =>
        Result.Failure<T>(
            ErrorCodesV1.DependencyUnavailable,
            description,
            retryAfterSeconds: 1);

    private static Result<T> CoordinationUnavailable<T>() =>
        Result.Failure<T>(
            ErrorCodesV1.CoordinationUnavailable,
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);
}
