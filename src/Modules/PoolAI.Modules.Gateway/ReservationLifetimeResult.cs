using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

public sealed record ReservationLifetimeResult(
    DispatchedReservationHandle Reservation,
    ReservationLifetimeStopReason StopReason,
    long SuccessfulRenewals,
    bool DrainTimedOut,
    bool SettledConservatively);
