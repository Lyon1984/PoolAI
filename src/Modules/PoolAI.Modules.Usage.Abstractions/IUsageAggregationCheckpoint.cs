namespace PoolAI.Modules.Usage.Abstractions;

public interface IUsageAggregationCheckpoint
{
    ValueTask<UsageAggregationClaimResult> ClaimAsync(
        UsageAggregationClaimRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> HeartbeatAsync(
        UsageAggregationLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<UsageAggregationLease?> AdvanceAsync(
        UsageAggregationAdvanceRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> ReleaseAsync(
        UsageAggregationLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
