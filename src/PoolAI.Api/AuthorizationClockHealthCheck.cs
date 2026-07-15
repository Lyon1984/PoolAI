using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Api;

internal sealed class AuthorizationClockHealthCheck(
    INtpOffsetProbe offsetProbe) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        NtpOffsetProbeResult result = await offsetProbe
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!result.IsAvailable)
        {
            return HealthCheckResult.Unhealthy("ntp_source_unavailable");
        }

        return NtpReadinessEvaluator.IsReady(result.Offset)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("clock_offset_exceeded");
    }
}
