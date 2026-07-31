using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Health;
using PoolAI.Modules.Supply.Infrastructure.Persistence;
using PoolAI.Modules.Supply.Infrastructure.Security;
using PoolAI.Modules.Supply.Infrastructure.Workers;
using PoolAI.Modules.Supply.Worker;

namespace PoolAI.Modules.Supply;

public static class DependencyInjection
{
#pragma warning disable MA0051 // The module Composition Root intentionally closes the full graph.
    public static IServiceCollection AddSupplyModule(
        this IServiceCollection services,
        IConfiguration configuration) =>
        AddSupplyModule(services, configuration, Environments.Production);

    public static IServiceCollection AddSupplyModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        AddModuleMarker(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new AccountControlPlanePolicy(
            ReadRequestHashPepper(configuration)));
        services.AddSingleton(
            AccountCredentialEnvelopeOptions.FromConfiguration(configuration));
        services.AddSingleton<IAccountCredentialProtector>(serviceProvider =>
            new AccountCredentialProtector(
                serviceProvider.GetRequiredService<
                    AccountCredentialEnvelopeOptions>(),
                serviceProvider.GetRequiredService<IOperationalEventWriter>()));
        services.TryAddSingleton<IAccountCredentialStore,
            PostgresAccountCredentialStore>();
        services.AddSingleton(
            AccountHealthProbeHttpOptions.FromConfiguration(
                configuration,
                environmentName));
        services.AddHttpClient(
                AccountHealthProbeHttpTransport.ClientName,
                static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                AccountHealthProbeHttpTransport.CreatePrimaryHandler(
                    serviceProvider.GetRequiredService<
                        AccountHealthProbeHttpOptions>()));
        services.AddSingleton<AccountHealthProbeHttpTransport>();
        services.AddSingleton<IAccountHealthProbeExecutor,
            AccountHealthProbeExecutor>();
        services.AddSingleton<IAccountHealthProbeCatalog,
            PostgresAccountHealthProbeCatalog>();
        services.AddSupplyInfrastructure();
        services.AddSingleton(static serviceProvider =>
            new GroupSupplyCommandCoordinator(
                serviceProvider.GetRequiredService<ICommandIdempotencyStore>(),
                serviceProvider.GetRequiredService<IAuditAppender>(),
                serviceProvider.GetRequiredService<IOutboxAppender>(),
                serviceProvider.GetRequiredService<AccountControlPlanePolicy>()));
        services.AddSingleton(static serviceProvider =>
            new AccountControlPlaneService(
                serviceProvider.GetRequiredService<IAccountControlPlaneRepository>(),
                serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
                serviceProvider.GetRequiredService<ICommandIdempotencyStore>(),
                serviceProvider.GetRequiredService<IAuditAppender>(),
                serviceProvider.GetRequiredService<IOutboxAppender>(),
                serviceProvider.GetRequiredService<IAccountCredentialProtector>(),
                serviceProvider.GetRequiredService<AccountControlPlanePolicy>()));
        services.AddSingleton(static serviceProvider =>
            new ChannelControlPlaneService(
                serviceProvider.GetRequiredService<IChannelControlPlaneRepository>(),
                serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
                serviceProvider.GetRequiredService<GroupSupplyCommandCoordinator>()));
        services.AddSingleton(static serviceProvider =>
            new GroupSupplyControlPlaneService(
                serviceProvider.GetRequiredService<
                    IGroupSupplyConfigurationRepository>(),
                serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
                serviceProvider.GetRequiredService<GroupSupplyCommandCoordinator>()));
        services.AddSingleton<IListAccountsUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<AccountControlPlaneService>());
        services.AddSingleton<IGetAccountUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<AccountControlPlaneService>());
        services.AddSingleton<ICreateAccountUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<AccountControlPlaneService>());
        services.AddSingleton<IUpdateAccountUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<AccountControlPlaneService>());
        services.AddSingleton<IRetireAccountUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<AccountControlPlaneService>());
        services.AddSingleton<IListChannelsUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<ChannelControlPlaneService>());
        services.AddSingleton<IGetChannelUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<ChannelControlPlaneService>());
        services.AddSingleton<ICreateChannelUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<ChannelControlPlaneService>());
        services.AddSingleton<IUpdateChannelUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<ChannelControlPlaneService>());
        services.AddSingleton<IRetireChannelUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<ChannelControlPlaneService>());
        services.AddSingleton<IGetGroupSupplyConfigurationUseCase>(
            static serviceProvider => serviceProvider.GetRequiredService<
                GroupSupplyControlPlaneService>());
        services.AddSingleton<ICreateGroupSupplyConfigurationUseCase>(
            static serviceProvider => serviceProvider.GetRequiredService<
                GroupSupplyControlPlaneService>());
        services.AddSingleton<IPatchGroupSupplyConfigurationUseCase>(
            static serviceProvider => serviceProvider.GetRequiredService<
                GroupSupplyControlPlaneService>());
        return services;
    }
#pragma warning restore MA0051

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

    private static void AddModuleMarker(IServiceCollection services)
    {
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Supply",
            HostCapability.Api | HostCapability.Worker));
    }

    private static byte[] ReadRequestHashPepper(IConfiguration configuration)
    {
        byte[] value;
        try
        {
            value = Convert.FromBase64String(
                configuration["Idempotency:RequestHashPepper"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper is invalid.",
                exception);
        }

        if (value.Length < 32)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper must contain at least 256 bits.");
        }

        return value;
    }
}
