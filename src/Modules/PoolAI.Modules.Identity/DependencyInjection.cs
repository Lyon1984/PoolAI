using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Infrastructure.Persistence;

namespace PoolAI.Modules.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Identity",
            HostCapability.Api | HostCapability.Worker));
        services.AddSingleton<IEmailOutboxDeliveryStore, PostgresEmailOutboxDeliveryStore>();
        return services;
    }
}
