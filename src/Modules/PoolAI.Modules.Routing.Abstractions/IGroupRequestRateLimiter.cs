namespace PoolAI.Modules.Routing.Abstractions;

public interface IGroupRequestRateLimiter
{
    ValueTask<Result<GroupRequestRateLimitPermit>> AcquireAsync(
        EntityId groupId,
        int requestsPerMinute,
        CancellationToken cancellationToken);
}
