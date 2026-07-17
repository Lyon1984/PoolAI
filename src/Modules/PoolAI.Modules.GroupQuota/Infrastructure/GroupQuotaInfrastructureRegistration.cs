using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.Modules.GroupQuota.Infrastructure;

internal static class GroupQuotaInfrastructureRegistration
{
    internal static IServiceCollection AddGroupQuotaInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IGroupRepository>(static serviceProvider =>
            new PostgresGroupRepository(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IGroupPoolSummaryReader>(static serviceProvider =>
            new PostgresGroupPoolSummaryReader(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
