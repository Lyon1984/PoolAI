using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;

namespace PoolAI.Application.Orchestration;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(GroupActivationOrchestrator).Assembly.GetName().Name!,
            "Cross-context application orchestration",
            HostCapability.Api));
        return services;
    }
}
