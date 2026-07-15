using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Usage.Abstractions;
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
        return services;
    }
}
