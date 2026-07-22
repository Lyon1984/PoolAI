using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Infrastructure;

namespace PoolAI.Modules.Supply;

public static class DependencyInjection
{
    public static IServiceCollection AddSupplyModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Supply",
            HostCapability.Api | HostCapability.Worker));
        services.TryAddSingleton<IGroupSupplyReadiness, FailClosedGroupSupplyReadiness>();
        return services;
    }
}
