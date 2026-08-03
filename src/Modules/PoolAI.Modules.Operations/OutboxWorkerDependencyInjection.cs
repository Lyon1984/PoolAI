using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Observability;
using PoolAI.Modules.Operations.Infrastructure.Persistence;
using PoolAI.Modules.Operations.Infrastructure.Workers;
using PoolAI.Modules.Operations.Worker;

namespace PoolAI.Modules.Operations;

public static class OutboxWorkerDependencyInjection
{
    public static IServiceCollection AddOperationsOutboxPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(OutboxPublisherOptions.FromConfiguration(configuration));
        services.TryAddSingleton<IOutboxRetryJitter, CryptoOutboxRetryJitter>();
        services.TryAddSingleton<
            IIntegrationEventConsumerExceptionClassifier,
            PostgresIntegrationEventConsumerExceptionClassifier>();
        services.TryAddSingleton<
            IQuotaDeliveryHealthReader,
            PostgresQuotaDeliveryHealthReader>();
        services.TryAddSingleton<IOutboxObservabilityStore, PostgresOutboxObservabilityStore>();
        services.TryAddSingleton<IntegrationEventDispatcher>();
        services.TryAddSingleton<OutboxPublisherMetrics>();
        services.TryAddSingleton<OutboxPublisherProcessor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, OutboxPublisherService>());
        return services;
    }
}
