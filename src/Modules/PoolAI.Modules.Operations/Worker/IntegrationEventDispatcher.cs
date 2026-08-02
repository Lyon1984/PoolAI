using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Worker;

internal sealed class IntegrationEventDispatcher
{
    private const string ConsumerExceptionReason = "consumer_exception";
    private const string DependencyUnavailableReason = "dependency_unavailable";
    private const string UnsupportedSchemaReason = "unsupported_schema_version";
    private const string UnregisteredTopicReason = "unregistered_topic";
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ConsumerRoute>> _routes;
    private readonly IIntegrationEventConsumerExceptionClassifier _exceptionClassifier;

    internal IntegrationEventDispatcher(
        IEnumerable<IIntegrationEventConsumer> consumers,
        IIntegrationEventConsumerExceptionClassifier exceptionClassifier)
    {
        ArgumentNullException.ThrowIfNull(consumers);
        _exceptionClassifier = exceptionClassifier
            ?? throw new ArgumentNullException(nameof(exceptionClassifier));
        IIntegrationEventConsumer[] materialized = consumers.ToArray();
        if (materialized.Length == 0)
        {
            throw new InvalidOperationException(
                "The Outbox publisher requires at least one explicit consumer route.");
        }

        HashSet<ConsumerRouteIdentity> identities = [];
        foreach (IIntegrationEventConsumer consumer in materialized)
        {
            ArgumentNullException.ThrowIfNull(consumer, nameof(consumers));
            IntegrationEventSubscription subscription = consumer.Subscription
                ?? throw new InvalidOperationException(
                    "An Integration Event consumer returned no subscription.");
            ConsumerRouteIdentity identity = new(
                subscription.ConsumerName,
                subscription.Topic,
                subscription.SchemaVersion);
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException(
                    $"Duplicate Integration Event consumer route '{subscription.ConsumerName}'.");
            }
        }

        _routes = materialized
            .Select(static consumer => new ConsumerRoute(consumer.Subscription, consumer))
            .GroupBy(static route => route.Subscription.Topic, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ConsumerRoute>)group
                    .OrderBy(static route => route.Subscription.ConsumerName, StringComparer.Ordinal)
                    .ThenBy(static route => route.Subscription.SchemaVersion)
                    .ToArray(),
                StringComparer.Ordinal);
        Topics = Array.AsReadOnly(_routes.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    internal IReadOnlyList<string> Topics { get; }

    internal async ValueTask<IntegrationEventConsumeResult> DispatchAsync(
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        OutboxMessageEnvelope envelope = message.Envelope;
        if (!_routes.TryGetValue(envelope.Topic, out IReadOnlyList<ConsumerRoute>? topicRoutes))
        {
            return IntegrationEventConsumeResult.Poison(UnregisteredTopicReason);
        }

        ConsumerRoute[] matchingRoutes = topicRoutes
            .Where(route => route.Subscription.SchemaVersion == envelope.SchemaVersion)
            .ToArray();
        if (matchingRoutes.Length == 0)
        {
            return IntegrationEventConsumeResult.Poison(UnsupportedSchemaReason);
        }

        bool allDuplicates = true;
        foreach (ConsumerRoute route in matchingRoutes)
        {
            IntegrationEventConsumeResult result = await ConsumeOneAsync(
                route,
                message,
                cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return IntegrationEventConsumeResult.Poison("invalid_consumer_result");
            }

            switch (result.Disposition)
            {
                case IntegrationEventConsumeDisposition.Processed:
                    allDuplicates = false;
                    break;
                case IntegrationEventConsumeDisposition.Duplicate:
                    break;
                case IntegrationEventConsumeDisposition.RetryableFailure:
                case IntegrationEventConsumeDisposition.Poison:
                    return result;
                default:
                    return IntegrationEventConsumeResult.Poison("invalid_consumer_result");
            }
        }

        return allDuplicates
            ? IntegrationEventConsumeResult.Duplicate
            : IntegrationEventConsumeResult.Processed;
    }

    private async ValueTask<IntegrationEventConsumeResult> ConsumeOneAsync(
        ConsumerRoute route,
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            return await route.Consumer
                .ConsumeAsync(message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (_exceptionClassifier.IsRetryable(exception))
        {
            return IntegrationEventConsumeResult.RetryableFailure(
                DependencyUnavailableReason);
        }
        catch (Exception)
        {
            return IntegrationEventConsumeResult.Poison(ConsumerExceptionReason);
        }
    }

    private sealed record ConsumerRoute(
        IntegrationEventSubscription Subscription,
        IIntegrationEventConsumer Consumer);

    private readonly record struct ConsumerRouteIdentity(
        string ConsumerName,
        string Topic,
        int SchemaVersion);
}
