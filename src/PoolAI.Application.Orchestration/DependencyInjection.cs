using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Application.Orchestration;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationOrchestration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(new ModuleRegistration(
            typeof(GroupActivationOrchestrator).Assembly.GetName().Name!,
            "Cross-context application orchestration",
            HostCapability.Api));
        services.TryAddSingleton<GroupActivationOrchestrator>();
        services.TryAddSingleton<IGroupActivationOrchestrator>(static serviceProvider =>
            serviceProvider.GetRequiredService<GroupActivationOrchestrator>());
        services.TryAddSingleton<IListUserGroupPoolsUseCase, UserGroupPoolQueryService>();
        services.TryAddSingleton<IApiKeyCreateUseCase, ApiKeyCreateOrchestrator>();
        services.TryAddSingleton<IApiKeyMutationUseCase, ApiKeyMutationOrchestrator>();
        return services;
    }
}
