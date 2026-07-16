using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Identity.Infrastructure.Security;

namespace PoolAI.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddModuleMarkerAndWorkerStore(services);
        return services;
    }

    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarkerAndWorkerStore(services);
        services.AddSingleton(IdentityOptions.FromConfiguration(configuration));
        services.AddSingleton(TokenHashOptions.FromConfiguration(configuration));
        services.AddSingleton(EnvelopeKeyRingOptions.FromConfiguration(configuration));
        services.AddSingleton(PasswordResetRateLimitOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdentityRepository>(
            IdentityServiceFactory.CreateRepository);
        services.AddSingleton<IVersionedPasswordHasher, VersionedPasswordHasher>();
        services.AddSingleton<IPasswordResetTokenHasher>(static serviceProvider =>
            new PasswordResetTokenHasher(
                serviceProvider.GetRequiredService<TokenHashOptions>()));
        services.AddSingleton<IEmailSecretEnvelope>(static serviceProvider =>
            new EmailSecretEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<IPasswordResetRateLimiter>(static serviceProvider =>
            new OperationsPasswordResetRateLimiter(
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.Operations.Abstractions.IFixedWindowCounter>(),
                serviceProvider.GetRequiredService<PasswordResetRateLimitOptions>()));
        services.AddSingleton(static serviceProvider => new IdentityUseCaseService(
            serviceProvider.GetRequiredService<IIdentityRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IOutboxAppender>(),
            serviceProvider.GetRequiredService<IVersionedPasswordHasher>(),
            serviceProvider.GetRequiredService<IPasswordResetTokenHasher>(),
            serviceProvider.GetRequiredService<IEmailSecretEnvelope>(),
            serviceProvider.GetRequiredService<IPasswordResetRateLimiter>(),
            serviceProvider.GetRequiredService<IdentityPolicy>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IListUsersUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<IGetUserUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<ICreateUserUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<IUpdateUserUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<IRequestAdminPasswordResetUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<IRequestPasswordResetUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        services.AddSingleton<ICompletePasswordResetUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<IdentityUseCaseService>());
        return services;
    }

    private static void AddModuleMarkerAndWorkerStore(IServiceCollection services)
    {
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Identity",
            HostCapability.Api | HostCapability.Worker));
        services.TryAddSingleton<IEmailOutboxDeliveryStore>(static _ =>
            new PostgresEmailOutboxDeliveryStore());
    }
}
