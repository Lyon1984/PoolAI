using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;

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
        return services;
    }
}
