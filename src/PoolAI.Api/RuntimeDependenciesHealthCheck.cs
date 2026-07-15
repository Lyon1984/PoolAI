using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Api;

internal sealed class RuntimeDependenciesHealthCheck(
    IRuntimeDependencyReadiness readiness) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        RuntimeDependencyReadiness result = await readiness
            .CheckAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsReady
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(result.FailureCode ?? "dependency_unavailable");
    }
}
