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

#pragma warning disable MA0051 // This method is the module Composition Root and intentionally exposes all registrations.
    public static IServiceCollection AddIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarkerAndWorkerStore(services);
        services.AddSingleton(IdentityOptions.FromConfiguration(configuration));
        services.AddSingleton(TokenHashOptions.FromConfiguration(configuration));
        services.AddSingleton(ApiKeyHashOptions.FromConfiguration(configuration));
        services.AddSingleton(EnvelopeKeyRingOptions.FromConfiguration(configuration));
        services.AddSingleton(PasswordResetRateLimitOptions.FromConfiguration(configuration));
        services.AddSingleton(LoginFailureRateLimitOptions.FromConfiguration(configuration));
        services.AddSingleton(SessionPolicy.FromConfiguration(configuration));
        services.AddSingleton(RefreshTokenHashOptions.FromConfiguration(configuration));
        services.AddSingleton(TotpOptions.FromConfiguration(configuration));
        services.AddSingleton(TotpRecoveryCodeHashOptions.FromConfiguration(configuration));
        services.AddSingleton(AccessTokenOptions.FromConfiguration(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IIdentityRepository>(
            IdentityServiceFactory.CreateRepository);
        services.AddSingleton<IIdentitySessionRepository>(
            IdentityServiceFactory.CreateSessionRepository);
        services.AddSingleton<IApiKeyRepository>(
            IdentityServiceFactory.CreateApiKeyRepository);
        services.AddSingleton<IUserStatusReader>(
            IdentityServiceFactory.CreateUserStatusReader);
        services.AddSingleton<IVersionedPasswordHasher, VersionedPasswordHasher>();
        services.AddSingleton<IPasswordResetTokenHasher>(static serviceProvider =>
            new PasswordResetTokenHasher(
                serviceProvider.GetRequiredService<TokenHashOptions>()));
        services.AddSingleton<IRefreshCredentialHasher>(static serviceProvider =>
            new RefreshCredentialHasher(
                serviceProvider.GetRequiredService<RefreshTokenHashOptions>()));
        services.AddSingleton<IApiKeyCredentialService>(static serviceProvider =>
            new ApiKeyCredentialService(
                serviceProvider.GetRequiredService<ApiKeyHashOptions>()));
        services.AddSingleton<IOneTimeChallengeHasher>(static serviceProvider =>
            new OneTimeChallengeHasher(
                serviceProvider.GetRequiredService<TokenHashOptions>()));
        services.AddSingleton<ITotpAuthenticator>(static serviceProvider =>
            new TotpAuthenticator(
                serviceProvider.GetRequiredService<TotpOptions>()));
        services.AddSingleton<ITotpSecretEnvelope>(static serviceProvider =>
            new TotpSecretEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<ITotpRecoveryCodeEnvelope>(static serviceProvider =>
            new TotpRecoveryCodeEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<ITotpSetupResponseEnvelope>(static serviceProvider =>
            new TotpSetupResponseEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<IApiKeyCreateResponseEnvelope>(static serviceProvider =>
            new ApiKeyCreateResponseEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<ITotpRecoveryCodeGenerator>(static serviceProvider =>
            new TotpRecoveryCodeGenerator(
                serviceProvider.GetRequiredService<TotpRecoveryCodeHashOptions>()));
        services.AddSingleton<IAccessTokenIssuer>(static serviceProvider =>
            new AccessTokenIssuer(
                serviceProvider.GetRequiredService<AccessTokenOptions>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IEmailSecretEnvelope>(static serviceProvider =>
            new EmailSecretEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>()));
        services.AddSingleton<IPasswordResetRateLimiter>(static serviceProvider =>
            new OperationsPasswordResetRateLimiter(
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.Operations.Abstractions.IFixedWindowCounter>(),
                serviceProvider.GetRequiredService<PasswordResetRateLimitOptions>()));
        services.AddSingleton<ILoginFailureRateLimiter>(static serviceProvider =>
            new OperationsLoginFailureRateLimiter(
                serviceProvider.GetRequiredService<
                    PoolAI.Modules.Operations.Abstractions.IFixedWindowCounter>(),
                serviceProvider.GetRequiredService<LoginFailureRateLimitOptions>()));
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
        services.AddSingleton(static serviceProvider => new ApiKeyUseCaseService(
            serviceProvider.GetRequiredService<IApiKeyRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<IApiKeyCredentialService>(),
            serviceProvider.GetRequiredService<IApiKeyCreateResponseEnvelope>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IOperationalEventWriter>(),
            serviceProvider.GetRequiredService<IdentityPolicy>()));
        services.AddSingleton<IApiKeyControlPlaneReader>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyUseCaseService>());
        services.AddSingleton<IApiKeyCreateIdempotencyPreflight>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyUseCaseService>());
        services.AddSingleton<IApiKeyIssuer>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyUseCaseService>());
        services.AddSingleton<IApiKeyMutationIdempotencyPreflight>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<ApiKeyUseCaseService>());
        services.AddSingleton<IApiKeyMutationOwner>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyUseCaseService>());
        services.AddSingleton(static serviceProvider => new ApiKeyAuthenticationService(
            serviceProvider.GetRequiredService<IApiKeyRepository>(),
            serviceProvider.GetRequiredService<IApiKeyCredentialService>()));
        services.AddSingleton<IApiKeyAuthenticator>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyAuthenticationService>());
        services.AddSingleton<ApiKeyCreatedOutcomeValidator>();
        services.AddSingleton<IApiKeyCreatedOutcomeValidator>(static serviceProvider =>
            serviceProvider.GetRequiredService<ApiKeyCreatedOutcomeValidator>());
        services.AddSingleton(static serviceProvider => new SessionAuthenticationUseCaseService(
            serviceProvider.GetRequiredService<IIdentitySessionRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<IVersionedPasswordHasher>(),
            serviceProvider.GetRequiredService<IRefreshCredentialHasher>(),
            serviceProvider.GetRequiredService<IOneTimeChallengeHasher>(),
            serviceProvider.GetRequiredService<ITotpAuthenticator>(),
            serviceProvider.GetRequiredService<ITotpSecretEnvelope>(),
            serviceProvider.GetRequiredService<IAccessTokenIssuer>(),
            serviceProvider.GetRequiredService<ILoginFailureRateLimiter>(),
            serviceProvider.GetRequiredService<SessionPolicy>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ILoginUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton<IVerifyLoginTotpUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton<IRefreshSessionUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton<ILogoutUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton<IGetCurrentUserUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton<IAccessSessionValidator>(static serviceProvider =>
            serviceProvider.GetRequiredService<SessionAuthenticationUseCaseService>());
        services.AddSingleton(static serviceProvider => new PersonalSecurityUseCaseService(
            serviceProvider.GetRequiredService<IIdentitySessionRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<IVersionedPasswordHasher>(),
            serviceProvider.GetRequiredService<IOneTimeChallengeHasher>(),
            serviceProvider.GetRequiredService<ITotpAuthenticator>(),
            serviceProvider.GetRequiredService<ITotpSecretEnvelope>(),
            serviceProvider.GetRequiredService<ITotpRecoveryCodeGenerator>(),
            serviceProvider.GetRequiredService<ITotpRecoveryCodeEnvelope>(),
            serviceProvider.GetRequiredService<ITotpSetupResponseEnvelope>(),
            serviceProvider.GetRequiredService<SessionPolicy>(),
            serviceProvider.GetRequiredService<IdentityPolicy>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IChangePasswordUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<PersonalSecurityUseCaseService>());
        services.AddSingleton<ISetupTotpUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<PersonalSecurityUseCaseService>());
        services.AddSingleton<IConfirmTotpUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<PersonalSecurityUseCaseService>());
        services.AddSingleton<IDisableTotpUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<PersonalSecurityUseCaseService>());
        return services;
    }
#pragma warning restore MA0051

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
