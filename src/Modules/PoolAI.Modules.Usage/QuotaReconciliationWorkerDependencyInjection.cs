using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.Modules.Usage.Infrastructure.Observability;
using PoolAI.Modules.Usage.Infrastructure.Workers;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage;

public static class QuotaReconciliationWorkerDependencyInjection
{
    public static IServiceCollection AddUsageQuotaReconciliationWorker(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<QuotaReconciliationMetrics>();
        services.TryAddSingleton<QuotaReconciliationProcessor>();
        services.TryAddSingleton<UsagePeriodProjectionRebuilder>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                QuotaReconciliationService>());
        return services;
    }

    public static IServiceCollection AddUsageProjectionRebuildWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        UsageProjectionRebuildWorkerOptions options =
            UsageProjectionRebuildWorkerOptions.FromConfiguration(configuration);
        EnsureConsistentRebuildOptions(services, options);
        if (!options.Enabled)
        {
            return services;
        }

        services.TryAddSingleton<UsagePeriodProjectionRebuilder>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                UsageProjectionRebuildOneShotService>());
        return services;
    }

    private static void EnsureConsistentRebuildOptions(
        IServiceCollection services,
        UsageProjectionRebuildWorkerOptions options)
    {
        ServiceDescriptor? existing = services.LastOrDefault(
            static descriptor => descriptor.ServiceType
                == typeof(UsageProjectionRebuildWorkerOptions));
        if (existing is null)
        {
            services.AddSingleton(options);
            return;
        }

        if (existing.ImplementationInstance
                is not UsageProjectionRebuildWorkerOptions registered
            || registered != options)
        {
            throw new InvalidOperationException(
                "The one-shot Usage rebuild was registered with inconsistent options.");
        }
    }
}
