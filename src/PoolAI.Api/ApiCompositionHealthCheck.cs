using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Api;

internal sealed class ApiCompositionHealthCheck(
    IEnumerable<ModuleRegistration> modules,
    AdapterCapabilityRegistry adapterCapabilities) : IHealthCheck
{
    private static readonly string[] RequiredContexts =
    [
        "Identity",
        "SubscriptionAccess",
        "GroupQuota",
        "Supply",
        "Routing",
        "Usage",
        "Operations",
        "Gateway",
        "Cross-context application orchestration",
    ];

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        HashSet<string> registered = modules
            .Select(module => module.BoundedContext)
            .ToHashSet(StringComparer.Ordinal);
        string[] missing = RequiredContexts
            .Where(contextName => !registered.Contains(contextName))
            .ToArray();

        if (missing.Length != 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Missing module registrations: {string.Join(", ", missing)}."));
        }

        _ = adapterCapabilities.Get(
            InboundProtocol.Responses,
            UpstreamType.OpenAi,
            AdapterOperation.NonStream);
        return Task.FromResult(HealthCheckResult.Healthy("M0 composition is complete."));
    }
}
