namespace PoolAI.Modules.Gateway.Application;

public sealed record ConservativeReservationSettlement(
    ReservationLifetimeStopReason Reason,
    bool DrainTimedOut);
