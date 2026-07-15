using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.Modules.Gateway;

public static class DependencyInjection
{
    public static IServiceCollection AddGatewayModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Gateway",
            HostCapability.Api));
        services.AddSingleton<AdapterCapabilityRegistry>();
        return services;
    }
}
