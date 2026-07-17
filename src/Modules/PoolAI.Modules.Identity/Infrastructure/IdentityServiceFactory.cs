using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Infrastructure.Persistence;

namespace PoolAI.Modules.Identity.Infrastructure;

internal static class IdentityServiceFactory
{
    internal static IIdentityRepository CreateRepository(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return new PostgresIdentityRepository(
            serviceProvider.GetRequiredService<NpgsqlDataSource>());
    }

    internal static IIdentitySessionRepository CreateSessionRepository(
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return new PostgresIdentitySessionRepository(
            serviceProvider.GetRequiredService<NpgsqlDataSource>());
    }

    internal static IUserStatusReader CreateUserStatusReader(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return new PostgresIdentitySessionReader(
            serviceProvider.GetRequiredService<NpgsqlDataSource>());
    }
}
