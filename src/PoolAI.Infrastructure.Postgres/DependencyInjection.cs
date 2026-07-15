using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;

namespace PoolAI.Infrastructure.Postgres;

public static class DependencyInjection
{
    public static IServiceCollection AddPoolAiPostgresRuntime(
        this IServiceCollection services,
        string connectionString,
        int commandTimeoutSeconds = 30,
        int maximumPoolSize = 100)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPoolSize, 1);

        NpgsqlConnectionStringBuilder connection = new(connectionString)
        {
            CommandTimeout = commandTimeoutSeconds,
            MaxPoolSize = maximumPoolSize,
        };
        services.AddSingleton(_ => NpgsqlDataSource.Create(connection.ConnectionString));
        services.AddSingleton<PostgresSessionAdvisoryLockProvider>(serviceProvider =>
            new PostgresSessionAdvisoryLockProvider(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IUnitOfWorkFactory>(serviceProvider =>
            new PostgresUnitOfWorkFactory(
                serviceProvider.GetRequiredService<NpgsqlDataSource>()));
        return services;
    }
}
