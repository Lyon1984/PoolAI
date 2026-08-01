using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway;

public static class DependencyInjection
{
    public static IServiceCollection AddGatewayModule(
        this IServiceCollection services,
        int disconnectDrainSeconds)
    {
        ArgumentNullException.ThrowIfNull(services);
        TimeSpan drainDuration = TimeSpan.FromSeconds(disconnectDrainSeconds);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Gateway",
            HostCapability.Api));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AdapterCapabilityRegistry>();
        services.AddSingleton(provider => new ReservationLifetimeCoordinator(
            provider.GetRequiredService<IGroupQuotaLedger>(),
            provider.GetRequiredService<TimeProvider>(),
            drainDuration));
        return services;
    }
}
