using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// The terminal, already-finalized result of one attempt. It is deliberately
/// not a retry loop; M4-E5 may consume this evidence to decide whether to start
/// a separately admitted later attempt.
/// </summary>
public sealed class GatewaySingleAttemptOutcome
{
    public GatewaySingleAttemptOutcome(
        EntityId requestId,
        EntityId attemptId,
        int attemptIndex,
        GatewayAttemptPhase phase,
        GatewaySingleAttemptDisposition disposition,
        NormalizedUpstreamResult? upstreamResult,
        ReservationLifetimeResult lifetime,
        AccountLeaseLifetimeStopReason accountLeaseStopReason,
        string? errorCode)
    {
        RequestId = requestId;
        AttemptId = attemptId;
        AttemptIndex = attemptIndex;
        Phase = phase;
        Disposition = disposition;
        UpstreamResult = upstreamResult;
        Lifetime = lifetime;
        AccountLeaseStopReason = accountLeaseStopReason;
        ErrorCode = errorCode;
    }

    public EntityId RequestId { get; }

    public EntityId AttemptId { get; }

    public int AttemptIndex { get; }

    public GatewayAttemptPhase Phase { get; }

    public GatewaySingleAttemptDisposition Disposition { get; }

    public NormalizedUpstreamResult? UpstreamResult { get; }

    public ReservationLifetimeResult Lifetime { get; }

    public AccountLeaseLifetimeStopReason AccountLeaseStopReason { get; }

    public string? ErrorCode { get; }

    public override string ToString() => nameof(GatewaySingleAttemptOutcome);
}
