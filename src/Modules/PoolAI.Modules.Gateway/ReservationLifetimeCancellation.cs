namespace PoolAI.Modules.Gateway.Application;

public readonly record struct ReservationLifetimeCancellation(
    CancellationToken AbortUpstream,
    CancellationToken Drain);
