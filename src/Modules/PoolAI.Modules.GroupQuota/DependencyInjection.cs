using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure;

namespace PoolAI.Modules.GroupQuota;

public static class DependencyInjection
{
    public static IServiceCollection AddGroupQuotaModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(DependencyInjection).Assembly.GetName().Name!,
            "GroupQuota",
            HostCapability.Api | HostCapability.Worker));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(static serviceProvider =>
            CreatePolicy(serviceProvider.GetRequiredService<IConfiguration>()));
        services.AddGroupQuotaInfrastructure();
        services.AddSingleton(static serviceProvider => new GroupControlPlaneService(
            serviceProvider.GetRequiredService<IGroupRepository>(),
            serviceProvider.GetRequiredService<IUnitOfWorkFactory>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.ICommandIdempotencyStore>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IAuditAppender>(),
            serviceProvider.GetRequiredService<
                PoolAI.Modules.Operations.Abstractions.IOutboxAppender>(),
            serviceProvider.GetRequiredService<GroupQuotaPolicy>(),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IListGroupsUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<IGetGroupUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<ICreateGroupUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<IUpdateGroupUseCase>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<IGroupStatusReader>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<IGroupActivationIdempotencyPreflight>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        services.AddSingleton<IGroupActivationCommand>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupControlPlaneService>());
        return services;
    }

    private static GroupQuotaPolicy CreatePolicy(IConfiguration configuration)
    {
        byte[] pepper;
        try
        {
            pepper = Convert.FromBase64String(
                configuration["Idempotency:RequestHashPepper"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper is invalid.",
                exception);
        }

        if (pepper.Length < 32)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper must contain at least 256 bits.");
        }

        return new GroupQuotaPolicy(pepper);
    }
}
