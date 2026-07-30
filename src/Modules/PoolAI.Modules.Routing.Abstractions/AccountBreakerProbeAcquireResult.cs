namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountBreakerProbeAcquireResult(
    AccountBreakerProbeAcquireDisposition Disposition,
    IAccountBreakerProbe? Probe,
    TimeSpan? RetryAfter)
{
    public static AccountBreakerProbeAcquireResult Acquired(
        IAccountBreakerProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return new(
            AccountBreakerProbeAcquireDisposition.Acquired,
            probe,
            RetryAfter: null);
    }

    public static AccountBreakerProbeAcquireResult NotEligible(
        TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            retryAfter,
            TimeSpan.Zero);

        return new(
            AccountBreakerProbeAcquireDisposition.NotEligible,
            Probe: null,
            retryAfter);
    }
}
