using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Endpoint-neutral input for one Responses or Chat Completions attempt.
/// Secret and forwarding-header values are intentionally excluded from the
/// generated string representation.
/// </summary>
internal sealed class GatewaySingleAttemptRequest
{
    internal GatewaySingleAttemptRequest(
        GatewayCanonicalAccess access,
        InboundProtocol protocol,
        NormalizedGatewayRequest request,
        int attemptIndex,
        string? clientRequestId,
        string leaseOwner,
        DateTimeOffset deadline,
        int remainingRetryBudget,
        string? sessionAffinityHash = null)
    {
        Access = access;
        Protocol = protocol;
        Request = request;
        AttemptIndex = attemptIndex;
        ClientRequestId = clientRequestId;
        LeaseOwner = leaseOwner;
        Deadline = deadline;
        RemainingRetryBudget = remainingRetryBudget;
        SessionAffinityHash = sessionAffinityHash;
    }

    internal GatewayCanonicalAccess Access { get; }

    internal InboundProtocol Protocol { get; }

    internal NormalizedGatewayRequest Request { get; }

    internal int AttemptIndex { get; }

    internal string? ClientRequestId { get; }

    internal string LeaseOwner { get; }

    internal DateTimeOffset Deadline { get; }

    internal int RemainingRetryBudget { get; }

    internal string? SessionAffinityHash { get; }

    public override string ToString() => nameof(GatewaySingleAttemptRequest);
}
