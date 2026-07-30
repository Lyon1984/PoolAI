using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Routing.Infrastructure;

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
        services.AddSingleton<IRouteAffinityStore, CoordinationRouteAffinityStore>();
        services.AddSingleton<IAccountRouter, AccountRouter>();
        services.AddSingleton<IGroupRequestRateLimiter, GroupRequestRateLimiter>();
        return services;
    }
}
