using Microsoft.Extensions.DependencyInjection;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Adapters.OpenAI;

public static class DependencyInjection
{
    public static IServiceCollection AddOpenAiAdapterCapabilities(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (AdapterCapability capability in OpenAiCapabilityDescriptor.R1Capabilities)
        {
            services.AddSingleton(capability);
        }

        return services;
    }
}
