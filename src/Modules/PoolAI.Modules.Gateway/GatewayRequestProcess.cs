using System.Net;
using System.Net.Sockets;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Owns the production composition boundary from canonical admission to the
/// single M4-E1 attempt. Authorization deliberately precedes authoritative
/// request-body validation and is carried across that boundary by an opaque,
/// one-use capability. Later-attempt admission remains an M4-E5 concern.
/// </summary>
public sealed class GatewayRequestProcess
{
    private const int InitialAttemptIndex = 0;
    private const int InitialRetryBudget = 0;
    private readonly GatewayCanonicalAdmissionService _canonicalAdmission;
    private readonly IGroupRequestRateLimiter _rateLimiter;
    private readonly IGatewaySingleAttemptExecutor _singleAttempt;

    internal GatewayRequestProcess(
        GatewayCanonicalAdmissionService canonicalAdmission,
        IGroupRequestRateLimiter rateLimiter,
        GatewaySingleAttemptProcessManager singleAttempt)
        : this(
            canonicalAdmission,
            rateLimiter,
            new GatewaySingleAttemptExecutor(singleAttempt))
    {
    }

    internal GatewayRequestProcess(
        GatewayCanonicalAdmissionService canonicalAdmission,
        IGroupRequestRateLimiter rateLimiter,
        IGatewaySingleAttemptExecutor singleAttempt)
    {
        _canonicalAdmission = canonicalAdmission
            ?? throw new ArgumentNullException(nameof(canonicalAdmission));
        _rateLimiter = rateLimiter
            ?? throw new ArgumentNullException(nameof(rateLimiter));
        _singleAttempt = singleAttempt
            ?? throw new ArgumentNullException(nameof(singleAttempt));
    }

    public async ValueTask<Result<GatewayAuthorizedRequest>> AuthorizeAsync(
        string presentedApiKey,
        IPAddress? socketPeer,
        IReadOnlyList<string>? forwardedForFieldValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentedApiKey);

        IPAddress? stableSocketPeer = CloneAddress(socketPeer);
        string[]? stableForwardedForFieldValues =
            forwardedForFieldValues?.ToArray();

        return await AuthorizeDependenciesAsync(
                presentedApiKey,
                stableSocketPeer,
                stableForwardedForFieldValues,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<Result<GatewayAuthorizedRequest>>
        AuthorizeDependenciesAsync(
            string presentedApiKey,
            IPAddress? socketPeer,
            IReadOnlyList<string>? forwardedForFieldValues,
            CancellationToken cancellationToken)
    {
        try
        {
            return await AuthorizeDependenciesCoreAsync(
                    presentedApiKey,
                    socketPeer,
                    forwardedForFieldValues,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result.Failure<GatewayAuthorizedRequest>(
                ErrorCodesV1.DependencyUnavailable,
                "Gateway admission dependencies are temporarily unavailable.",
                retryAfterSeconds: 1);
        }
    }

    private async ValueTask<Result<GatewayAuthorizedRequest>>
        AuthorizeDependenciesCoreAsync(
            string presentedApiKey,
            IPAddress? socketPeer,
            IReadOnlyList<string>? forwardedForFieldValues,
            CancellationToken cancellationToken)
    {
        Result<GatewayCanonicalAccess> canonical = await _canonicalAdmission
            .AuthorizeAsync(
                presentedApiKey,
                socketPeer,
                forwardedForFieldValues,
                cancellationToken)
            .ConfigureAwait(false);
        if (canonical.IsFailure)
        {
            return CopyFailure<GatewayAuthorizedRequest>(canonical.Error);
        }

        Result<GroupRequestRateLimitPermit> rpm = await _rateLimiter.AcquireAsync(
                canonical.Value.Group.GroupId,
                canonical.Value.Group.RequestsPerMinute,
                cancellationToken)
            .ConfigureAwait(false);
        if (rpm.IsFailure)
        {
            return CopyFailure<GatewayAuthorizedRequest>(rpm.Error);
        }

        return Result.Success(new GatewayAuthorizedRequest(
            this,
            canonical.Value));
    }

    public ValueTask<Result<GatewaySingleAttemptOutcome>>
        ExecuteInitialAttemptAsync(
            GatewayAuthorizedRequest authorization,
            InboundProtocol protocol,
            NormalizedGatewayRequest request,
            string? clientRequestId,
            DateTimeOffset deadline,
            string? sessionAffinityHash,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(request);

        if (!authorization.TryConsume(this, out GatewayCanonicalAccess access))
        {
            return ValueTask.FromResult(Result.Failure<
                GatewaySingleAttemptOutcome>(
                ErrorCodesV1.InvalidRequest,
                "The Gateway authorization capability is invalid or has already been consumed."));
        }

        GatewaySingleAttemptRequest initialAttempt = new(
            access,
            protocol,
            request,
            InitialAttemptIndex,
            clientRequestId,
            CreateReservationLeaseOwner(request.RequestId),
            deadline,
            InitialRetryBudget,
            sessionAffinityHash);
        return _singleAttempt.ExecuteAsync(initialAttempt, cancellationToken);
    }

    public override string ToString() => nameof(GatewayRequestProcess);

    private static string CreateReservationLeaseOwner(EntityId requestId) =>
        string.Concat(
            "gateway:",
            requestId.Value.ToString("N"),
            ":0");

    private static IPAddress? CloneAddress(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(bytes, address.ScopeId)
            : new IPAddress(bytes);
    }

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);
}
