using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Routing.Infrastructure.Workers;

internal sealed record AccountHealthWorkerOptions(
    TimeSpan ProbeInterval,
    int MaximumConcurrency)
{
    internal static AccountHealthWorkerOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int probeIntervalSeconds = configuration.GetValue(
            "Supply:Health:ProbeIntervalSeconds",
            30);
        int maximumConcurrency = configuration.GetValue(
            "Supply:Health:ProbeMaxConcurrency",
            8);
        if (probeIntervalSeconds != 30)
        {
            throw new InvalidOperationException(
                "Supply health probe interval must equal thirty seconds.");
        }

        if (maximumConcurrency != 8)
        {
            throw new InvalidOperationException(
                "Supply health probe concurrency must equal eight.");
        }

        return new(
            TimeSpan.FromSeconds(probeIntervalSeconds),
            maximumConcurrency);
    }
}
