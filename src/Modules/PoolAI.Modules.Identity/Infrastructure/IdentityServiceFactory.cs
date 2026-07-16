using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
}
