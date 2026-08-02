using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Infrastructure.Persistence;

namespace PoolAI.Modules.Usage;

public static class DependencyInjection
{
    public static IServiceCollection AddUsageModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Usage",
            HostCapability.Api | HostCapability.Worker));
        services.AddSingleton<IUsageAggregationCheckpoint, PostgresUsageAggregationCheckpoint>();
        services.AddSingleton<IUsageHourlyProjectionWriter,
            PostgresUsageHourlyProjectionWriter>();
        services.AddSingleton<IIntegrationEventConsumer>(static serviceProvider =>
            new GroupQuotaUsageProjectorConsumer(
                serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
                serviceProvider.GetRequiredService<IInboxReceiptAppender>(),
                serviceProvider.GetRequiredService<IInboxReplayPredecessorVerifier>(),
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.GroupQuota.Abstractions.
                        IGroupQuotaEventFactReader>(),
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.GroupQuota.Abstractions.
                        IAttemptSettlementHourFactReader>(),
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.GroupQuota.Abstractions.
                        IAttemptSettlementFactExistenceReader>(),
                serviceProvider.GetRequiredService<IUsageHourlyProjectionWriter>(),
                serviceProvider.GetRequiredService<IUsageAggregationCheckpoint>()));
        return services;
    }
}
