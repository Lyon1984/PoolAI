namespace PoolAI.Modules.Identity.Abstractions;

public sealed record EmailOutboxObservabilitySnapshot(
    long PendingCount,
    double OldestAgeSeconds,
    long DeadCount,
    IReadOnlyList<EmailOutboxFailureMetric> Failures)
{
    public static EmailOutboxObservabilitySnapshot Empty { get; } = new(0, 0, 0, []);
}
