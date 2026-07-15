using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Routing;

public static class DependencyInjection
{
    public static IServiceCollection AddRoutingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Routing",
            HostCapability.Api));
        return services;
    }
}
