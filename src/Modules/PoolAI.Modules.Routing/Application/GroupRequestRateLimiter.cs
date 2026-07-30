using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Routing.Application;

internal sealed class GroupRequestRateLimiter(
    IFixedWindowCounter counter) : IGroupRequestRateLimiter
{
    private readonly IFixedWindowCounter _counter =
        counter ?? throw new ArgumentNullException(nameof(counter));

    public async ValueTask<Result<GroupRequestRateLimitPermit>> AcquireAsync(
        EntityId groupId,
        int requestsPerMinute,
        CancellationToken cancellationToken)
    {
        if (requestsPerMinute <= 0)
        {
            return Result.Failure<GroupRequestRateLimitPermit>(
                "invalid_request",
                "The Group requests-per-minute limit must be positive.");
        }

        FixedWindowCounterResult result = await _counter
            .IncrementAsync(
                new FixedWindowCounterRequest(
                    $"rate:group:v1:{{{groupId.Value:D}}}",
                    requestsPerMinute),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Disposition switch
        {
            FixedWindowCounterDisposition.Allowed =>
                Result.Success(new GroupRequestRateLimitPermit(result.Current)),
            FixedWindowCounterDisposition.Rejected =>
                Result.Failure<GroupRequestRateLimitPermit>(
                    "group_rate_limited",
                    "The Group requests-per-minute limit has been exceeded.",
                    RetrySeconds(result.RetryAfter)),
            _ => Result.Failure<GroupRequestRateLimitPermit>(
                "coordination_unavailable",
                "Redis coordination is temporarily unavailable.",
                retryAfterSeconds: 1),
        };
    }

    private static long RetrySeconds(TimeSpan retryAfter) =>
        Math.Max(1, checked((long)Math.Ceiling(retryAfter.TotalSeconds)));
}
