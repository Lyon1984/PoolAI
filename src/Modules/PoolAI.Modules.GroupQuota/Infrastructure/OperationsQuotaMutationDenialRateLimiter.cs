using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure;

internal sealed class OperationsQuotaMutationDenialRateLimiter(
    IFixedWindowCounter counter,
    QuotaMutationDenialRateLimitOptions options) : IQuotaMutationDenialRateLimiter
{
    private readonly IFixedWindowCounter _counter =
        counter ?? throw new ArgumentNullException(nameof(counter));
    private readonly QuotaMutationDenialRateLimitOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<QuotaMutationDenialRateLimitDecision> AcquireAsync(
        EntityId actorUserId,
        CancellationToken cancellationToken)
    {
        if (actorUserId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The quota-denial actor identifier is required.",
                nameof(actorUserId));
        }

        FixedWindowCounterResult result = await _counter
            .IncrementAsync(
                new FixedWindowCounterRequest(
                    $"rate:quota-denial:v1:{{{actorUserId.Value:D}}}",
                    _options.AttemptsPerMinute),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Disposition switch
        {
            FixedWindowCounterDisposition.Allowed =>
                QuotaMutationDenialRateLimitDecision.Allowed,
            FixedWindowCounterDisposition.Rejected =>
                QuotaMutationDenialRateLimitDecision.Rejected(
                    CeilingSeconds(result.RetryAfter)),
            FixedWindowCounterDisposition.Unavailable =>
                QuotaMutationDenialRateLimitDecision.Unavailable,
            _ => throw new InvalidOperationException(
                "The fixed-window counter returned an unknown disposition."),
        };
    }

    private static long CeilingSeconds(TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return checked(
            (retryAfter.Ticks + TimeSpan.TicksPerSecond - 1)
            / TimeSpan.TicksPerSecond);
    }
}
