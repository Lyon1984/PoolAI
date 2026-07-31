using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Health;
using PoolAI.Modules.Supply.Infrastructure.Persistence;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal static class SupplyInfrastructureRegistration
{
    internal static IServiceCollection AddSupplyInfrastructure(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAccountControlPlaneRepository,
            PostgresAccountControlPlaneRepository>();
        services.AddSingleton<IChannelControlPlaneRepository>(
            static serviceProvider => new PostgresChannelControlPlaneRepository(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(static serviceProvider =>
            new PostgresGroupSupplyConfigurationRepository(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IGroupSupplyConfigurationRepository>(
            static serviceProvider => serviceProvider.GetRequiredService<
                PostgresGroupSupplyConfigurationRepository>());
        services.AddSingleton<IGroupSupplyConfigurationReader>(
            static serviceProvider => serviceProvider.GetRequiredService<
                PostgresGroupSupplyConfigurationRepository>());
        services.AddSingleton<IGroupSupplyReadiness>(static serviceProvider =>
            new PostgresGroupSupplyReadiness(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IAccountCandidateReader>(static serviceProvider =>
            new PostgresAccountCandidateReader(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IAccountHealthWriter>(static serviceProvider =>
            new PostgresAccountHealthWriter(
                serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
                serviceProvider.GetRequiredService<IAuditAppender>()));
        services.AddSingleton<IAccountHealthProbeSnapshotReader>(
            static serviceProvider =>
                new PostgresAccountHealthProbeSnapshotReader(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IModelCatalog>(static serviceProvider =>
            new PostgresModelCatalog(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
