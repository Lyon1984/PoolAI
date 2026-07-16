namespace PoolAI.Modules.Identity.Abstractions;

public interface IEmailOutboxDeliveryStore
{
    ValueTask<IReadOnlyList<EmailOutboxMessage>> ClaimDueAsync(
        EntityId owner,
        int maximumCount,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> HeartbeatAsync(
        EmailOutboxDeliveryLease lease,
        TimeSpan leaseDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> MarkSentAsync(
        EmailOutboxDeliveryLease lease,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> ReleaseForRetryAsync(
        EmailOutboxDeliveryLease lease,
        TimeSpan retryDelay,
        string failureClass,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> MarkDeadAsync(
        EmailOutboxDeliveryLease lease,
        string failureClass,
        string terminalReason,
        string errorSummary,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<EmailOutboxObservabilitySnapshot> ReadObservabilityAsync(
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
