using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Infrastructure;

namespace PoolAI.Modules.SubscriptionAccess;

public static class DependencyInjection
{
    public static IServiceCollection AddSubscriptionAccessModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddModuleMarker(services);
        return services;
    }

#pragma warning disable MA0051 // The module Composition Root intentionally closes the full graph.
    public static IServiceCollection AddSubscriptionAccessModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarker(services);
        services.AddSingleton(new SubscriptionPolicy(ReadRequestHashPepper(configuration)));
        services.AddSubscriptionAccessInfrastructure();
        services.AddSingleton(static serviceProvider => new SubscriptionUseCaseService(
            serviceProvider.GetRequiredService<ISubscriptionRepository>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Identity.Abstractions.IUserStatusReader>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IOutboxAppender>(),
            serviceProvider.GetRequiredService<SubscriptionPolicy>()));
        services.AddSingleton<IListSubscriptionTemplatesUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IGetSubscriptionTemplateUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<ICreateSubscriptionTemplateUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IUpdateSubscriptionTemplateUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IRetireSubscriptionTemplateUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IListSubscriptionsUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IGetSubscriptionUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IAssignSubscriptionUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IUpdateSubscriptionUseCase>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<ISubscriptionAccessReader>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        services.AddSingleton<IUserSubscriptionGrantReader>(static provider =>
            provider.GetRequiredService<SubscriptionUseCaseService>());
        return services;
    }
#pragma warning restore MA0051

    private static void AddModuleMarker(IServiceCollection services) =>
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "SubscriptionAccess",
            HostCapability.Api));

    private static byte[] ReadRequestHashPepper(IConfiguration configuration)
    {
        string encoded = configuration["Idempotency:RequestHashPepper"] ?? string.Empty;
        byte[] value;
        try
        {
            value = Convert.FromBase64String(encoded);
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
