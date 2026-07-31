using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Routing.Infrastructure;
using PoolAI.Modules.Routing.Infrastructure.Workers;
using PoolAI.Modules.Routing.Worker;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing;

public static class DependencyInjection
{
    public static IServiceCollection AddRoutingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddModuleMarker(services);
        AddHealthCore(services);
        services.AddSingleton<IAccountActiveLeaseReader,
            AccountActiveLeaseReader>();
        services.AddSingleton<IRouteAffinityStore, CoordinationRouteAffinityStore>();
        services.AddSingleton<IAccountRouter, AccountRouter>();
        services.AddSingleton<IGroupRequestRateLimiter, GroupRequestRateLimiter>();
        return services;
    }

    public static IServiceCollection AddRoutingHealthModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarker(services);
        AddHealthCore(services);
        services.TryAddSingleton(
            AccountHealthWorkerOptions.FromConfiguration(configuration));
        services.TryAddSingleton<ISupplyHealthReadinessSummaryStore,
            SupplyHealthReadinessSummaryStore>();
        services.TryAddSingleton<AccountHealthProbeProcessor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                AccountHealthWorkerService>());
        return services;
    }

    private static void AddHealthCore(IServiceCollection services)
    {
        services.TryAddSingleton<IAccountCircuitBreaker, AccountCircuitBreaker>();
        services.TryAddSingleton<IAccountProbeLeaseCoordinator,
            AccountProbeLeaseCoordinator>();
    }

    private static void AddModuleMarker(IServiceCollection services)
    {
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Routing",
            HostCapability.Api | HostCapability.Worker));
    }
}
