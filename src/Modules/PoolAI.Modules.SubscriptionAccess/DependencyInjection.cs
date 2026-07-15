using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.SubscriptionAccess;

public static class DependencyInjection
{
    public static IServiceCollection AddSubscriptionAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "SubscriptionAccess",
            HostCapability.Api));
        return services;
    }
}
