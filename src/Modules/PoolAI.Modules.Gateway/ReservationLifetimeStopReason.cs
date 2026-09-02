namespace PoolAI.Modules.Gateway.Application;

public enum ReservationLifetimeStopReason
{
    Completed,
    UpstreamCompletedWithoutUsage,
    UpstreamFaulted,
    ClientDisconnected,
    AttemptDeadlineReached,
    RenewalFailed,
    HardDeadlineReached,
}
