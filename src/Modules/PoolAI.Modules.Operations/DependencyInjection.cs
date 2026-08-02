using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Application;
using PoolAI.Modules.Operations.Application.Ports;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Configuration;
using PoolAI.Modules.Operations.Infrastructure.Persistence;
using PoolAI.Modules.Operations.Infrastructure.Redis;
using PoolAI.Modules.Operations.Infrastructure.Workers;

namespace PoolAI.Modules.Operations;

public static class DependencyInjection
{
    public static IServiceCollection AddOperationsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Operations",
            HostCapability.Api | HostCapability.Worker));
        Assembly assembly = typeof(DependencyInjection).Assembly;
        ReleaseManifestV1 releaseManifest = ReleaseManifestV1Loader.LoadEmbedded(
            assembly,
            "poolai-release-manifest-v1.json");
        services.AddSingleton(releaseManifest);
        services.AddSingleton(RedisScriptCatalog.Load(releaseManifest.Redis, assembly));
        services.AddSingleton(serviceProvider =>
        {
            IConfiguration runtimeConfiguration = serviceProvider
                .GetRequiredService<IConfiguration>();
            return CreateRuntimeDependencyOptions(runtimeConfiguration, environmentName);
        });
        services.AddSingleton(serviceProvider =>
        {
            IConfiguration runtimeConfiguration = serviceProvider
                .GetRequiredService<IConfiguration>();
            return new NtpProbeOptions(
                runtimeConfiguration["Health:Ntp:Server"]!,
                runtimeConfiguration.GetValue("Health:Ntp:Port", 123),
                TimeSpan.FromMilliseconds(runtimeConfiguration.GetValue(
                    "Health:Ntp:TimeoutMilliseconds",
                    750)));
        });
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICommandIdempotencyStore, PostgresCommandIdempotencyStore>();
        services.AddSingleton<PostgresAuditAppender>();
        services.AddSingleton<IAuditAppender>(static serviceProvider =>
            serviceProvider.GetRequiredService<PostgresAuditAppender>());
        services.AddSingleton<IIdempotentAuditAppender>(static serviceProvider =>
            serviceProvider.GetRequiredService<PostgresAuditAppender>());
        services.AddSingleton<IOutboxAppender, PostgresOutboxAppender>();
        services.AddSingleton<PostgresInboxReceiptAppender>();
        services.AddSingleton<IInboxReceiptAppender>(static serviceProvider =>
            serviceProvider.GetRequiredService<PostgresInboxReceiptAppender>());
        services.AddSingleton<IInboxReplayPredecessorVerifier>(static serviceProvider =>
            serviceProvider.GetRequiredService<PostgresInboxReceiptAppender>());
        services.AddSingleton<IOutboxDeliveryStore, PostgresOutboxDeliveryStore>();
        services.AddSingleton<IWorkerSessionLockProvider, PostgresWorkerSessionLockProvider>();
        services.AddSingleton<IOperationalEventWriter, LoggingOperationalEventWriter>();
        services.AddSingleton<RedisConnectionProvider>();
        services.AddSingleton<RedisScriptRegistry>();
        services.AddSingleton<RedisScriptEvaluator>();
        services.AddSingleton<ICoordinationLeaseSet, RedisCoordinationLeaseSet>();
        services.AddSingleton<ICoordinationLeaseCounter,
            RedisCoordinationLeaseCounter>();
        services.AddSingleton<ICoordinationCircuitBreaker, RedisCoordinationCircuitBreaker>();
        services.AddSingleton<ICoordinationValueStore, RedisCoordinationValueStore>();
        services.AddSingleton<IFixedWindowCounter, RedisFixedWindowCounter>();
        services.AddSingleton<IRuntimeDependencyReadiness, RuntimeDependencyReadinessProbe>();
        services.AddSingleton<INtpOffsetProbe, SntpOffsetProbe>();
        return services;
    }

    public static IServiceCollection AddOperationsAdminControlPlane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton(_ => CreateOutboxReplayPolicy(configuration));
        services.AddSingleton<IOutboxReplayRepository, PostgresOutboxReplayRepository>();
        services.AddSingleton(static serviceProvider => new OutboxReplayService(
            serviceProvider.GetRequiredService<IOutboxReplayRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<IAuditAppender>(),
            serviceProvider.GetRequiredService<OutboxReplayPolicy>()));
        services.AddSingleton<IReplayDeadOutboxUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<OutboxReplayService>());
        return services;
    }

    internal static RuntimeDependencyOptions CreateRuntimeDependencyOptions(
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        return new RuntimeDependencyOptions(
            configuration["Data:Redis:ConnectionString"]!,
            configuration["Data:Redis:KeyPrefix"]
                ?? PoolAiRuntimeConfigurationDefaults.RedisKeyPrefix(environmentName),
            TimeSpan.FromSeconds(configuration.GetValue(
                "Health:ReadinessTimeoutSeconds",
                3)));
    }

    private static OutboxReplayPolicy CreateOutboxReplayPolicy(
        IConfiguration configuration)
    {
        byte[] pepper;
        try
        {
            pepper = Convert.FromBase64String(
                configuration["Idempotency:RequestHashPepper"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper is invalid.",
                exception);
        }

        try
        {
            return new OutboxReplayPolicy(pepper);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pepper);
        }
    }
}
