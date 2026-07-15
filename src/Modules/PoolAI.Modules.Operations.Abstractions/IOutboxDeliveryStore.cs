namespace PoolAI.Modules.Operations.Abstractions;

public interface IOutboxDeliveryStore
{
    ValueTask<IReadOnlyList<OutboxMessageEnvelope>> ClaimDueAsync(
        EntityId owner,
        int maximumCount,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> HeartbeatAsync(
        OutboxDeliveryLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> MarkPublishedAsync(
        OutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> ReleaseForRetryAsync(
        OutboxDeliveryLease lease,
        TimeSpan retryDelay,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> MarkDeadAsync(
        OutboxDeliveryLease lease,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<OutboxReplayReceipt?> ReplayDeadAsync(
        OutboxReplayRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
