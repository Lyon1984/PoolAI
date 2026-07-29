using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure;
using PoolAI.Modules.Supply.Infrastructure.Security;

namespace PoolAI.Modules.Supply;

public static class DependencyInjection
{
    public static IServiceCollection AddSupplyModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        AddModuleMarkerAndReadiness(services);
        services.AddSingleton(
            AccountCredentialEnvelopeOptions.FromConfiguration(configuration));
        services.AddSingleton<IAccountCredentialProtector>(serviceProvider =>
            new AccountCredentialProtector(
                serviceProvider.GetRequiredService<
                    AccountCredentialEnvelopeOptions>(),
                serviceProvider.GetRequiredService<IOperationalEventWriter>()));
        return services;
    }

    private static void AddModuleMarkerAndReadiness(IServiceCollection services)
    {
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "Supply",
            HostCapability.Api | HostCapability.Worker));
        services.TryAddSingleton<IGroupSupplyReadiness, FailClosedGroupSupplyReadiness>();
    }
}
