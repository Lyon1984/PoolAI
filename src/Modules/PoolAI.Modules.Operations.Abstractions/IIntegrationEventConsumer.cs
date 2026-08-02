namespace PoolAI.Modules.Operations.Abstractions;

public interface IIntegrationEventConsumer
{
    IntegrationEventSubscription Subscription { get; }

    ValueTask<IntegrationEventConsumeResult> ConsumeAsync(
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken);
}
