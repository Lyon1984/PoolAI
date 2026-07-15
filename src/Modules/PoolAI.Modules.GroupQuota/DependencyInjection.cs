using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.GroupQuota;

public static class DependencyInjection
{
    public static IServiceCollection AddGroupQuotaModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "GroupQuota",
            HostCapability.Api | HostCapability.Worker));
        return services;
    }
}
