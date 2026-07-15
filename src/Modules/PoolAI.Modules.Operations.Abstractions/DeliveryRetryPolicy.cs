namespace PoolAI.Modules.Operations.Abstractions;

public sealed record DeliveryRetryPolicy
{
    public DeliveryRetryPolicy(
        int maximumAttempts,
        TimeSpan baseDelay,
        TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(baseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, baseDelay);

        MaximumAttempts = maximumAttempts;
        BaseDelay = baseDelay;
        MaximumDelay = maximumDelay;
    }

    public int MaximumAttempts { get; }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaximumDelay { get; }

    public DeliveryFailureDecision Decide(int attempt, double jitterFraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);

        if (jitterFraction is < 0 or > 0.1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFraction));
        }

        if (attempt >= MaximumAttempts)
        {
            return DeliveryFailureDecision.Dead;
        }

        double multiplier = Math.Pow(2, Math.Min(attempt - 1, 62));
        double exponentialTicks = Math.Min(
            MaximumDelay.Ticks,
            BaseDelay.Ticks * multiplier);
        long delayedTicks = checked((long)Math.Min(
            MaximumDelay.Ticks,
            exponentialTicks * (1 + jitterFraction)));
        return DeliveryFailureDecision.Retry(TimeSpan.FromTicks(delayedTicks));
    }
}
