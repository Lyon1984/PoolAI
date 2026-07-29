using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure;
using PoolAI.Modules.Supply.Infrastructure.Persistence;
using PoolAI.Modules.Supply.Infrastructure.Security;
using PoolAI.Modules.Supply.Infrastructure.Workers;
using PoolAI.Modules.Supply.Worker;

namespace PoolAI.Modules.Supply;

public static class DependencyInjection
{
    public static IServiceCollection AddSupplyModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarkerAndReadiness(services);
        services.AddSingleton(
            AccountCredentialEnvelopeOptions.FromConfiguration(configuration));
        services.AddSingleton<IAccountCredentialProtector>(serviceProvider =>
            new AccountCredentialProtector(
                serviceProvider.GetRequiredService<
                    AccountCredentialEnvelopeOptions>(),
                serviceProvider.GetRequiredService<IOperationalEventWriter>()));
        services.TryAddSingleton<IAccountCredentialStore,
            PostgresAccountCredentialStore>();
        return services;
    }

    public static IServiceCollection AddSupplyCredentialRewrapWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AccountCredentialRewrapWorkerOptions options =
            AccountCredentialRewrapWorkerOptions.FromConfiguration(configuration);
        EnsureConsistentRewrapOptions(services, options);
        if (!options.Enabled)
        {
            return services;
        }

        services.TryAddSingleton<AccountCredentialRewrapProcessor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                AccountCredentialRewrapService>());
        return services;
    }

    private static void EnsureConsistentRewrapOptions(
        IServiceCollection services,
        AccountCredentialRewrapWorkerOptions options)
    {
        ServiceDescriptor? existing = services.LastOrDefault(
            static descriptor =>
                descriptor.ServiceType
                    == typeof(AccountCredentialRewrapWorkerOptions));
        if (existing is null)
        {
            services.AddSingleton(options);
            return;
        }

        if (existing.ImplementationInstance
                is not AccountCredentialRewrapWorkerOptions registered
            || registered != options)
        {
            throw new InvalidOperationException(
                "Account credential rewrap was registered with inconsistent options.");
        }
    }

    private static void AddModuleMarkerAndReadiness(IServiceCollection services)
    {
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Supply",
            HostCapability.Api | HostCapability.Worker));
        services.TryAddSingleton<IGroupSupplyReadiness, FailClosedGroupSupplyReadiness>();
    }
}
