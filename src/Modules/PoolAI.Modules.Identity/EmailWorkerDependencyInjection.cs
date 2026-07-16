using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Infrastructure.Email;
using PoolAI.Modules.Identity.Infrastructure.Persistence;
using PoolAI.Modules.Identity.Infrastructure.Security;
using PoolAI.Modules.Identity.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity;

public static class EmailWorkerDependencyInjection
{
    public static IServiceCollection AddIdentityEmailOutboxWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(EmailOutboxWorkerOptions.FromConfiguration(configuration));
        services.TryAddSingleton<EnvelopeKeyRingOptions>(
            _ => EnvelopeKeyRingOptions.FromConfiguration(configuration));
        services.Replace(ServiceDescriptor.Singleton<IEmailSecretEnvelope>(serviceProvider =>
            new EmailSecretEnvelopeV1(
                serviceProvider.GetRequiredService<EnvelopeKeyRingOptions>())));
        services.Replace(ServiceDescriptor.Singleton<IEmailOutboxDeliveryStore>(
            static _ => new PostgresEmailOutboxDeliveryStore()));
        services.TryAddSingleton<IEmailTransport>(serviceProvider =>
            new SmtpEmailTransport(
                serviceProvider.GetRequiredService<EmailOutboxWorkerOptions>()));
        services.TryAddSingleton<IEmailRetryJitter>(static _ => new CryptoEmailRetryJitter());
        services.TryAddSingleton(serviceProvider => new EmailOutboxMetrics(
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<IEmailOutboxDeliveryStore>()));
        services.TryAddSingleton(serviceProvider => new EmailOutboxProcessor(
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<IEmailOutboxDeliveryStore>(),
            serviceProvider.GetRequiredService<IEmailSecretEnvelope>(),
            serviceProvider.GetRequiredService<IEmailTransport>(),
            serviceProvider.GetRequiredService<IEmailRetryJitter>(),
            serviceProvider.GetRequiredService<IOperationalEventWriter>(),
            serviceProvider.GetRequiredService<EmailOutboxWorkerOptions>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, EmailOutboxSenderService>());
        return services;
    }
}
