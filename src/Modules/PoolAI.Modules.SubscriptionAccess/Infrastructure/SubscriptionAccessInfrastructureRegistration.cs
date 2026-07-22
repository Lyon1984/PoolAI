using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Infrastructure.Persistence;

namespace PoolAI.Modules.SubscriptionAccess.Infrastructure;

internal static class SubscriptionAccessInfrastructureRegistration
{
    internal static IServiceCollection AddSubscriptionAccessInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISubscriptionRepository>(static serviceProvider =>
            new PostgresSubscriptionRepository(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
