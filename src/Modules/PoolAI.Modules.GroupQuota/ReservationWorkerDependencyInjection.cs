using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PoolAI.Modules.GroupQuota.Infrastructure.Workers;
using PoolAI.Modules.GroupQuota.Worker;

namespace PoolAI.Modules.GroupQuota;

public static class ReservationWorkerDependencyInjection
{
    private const int RequiredSweepSeconds = 30;

    public static IServiceCollection AddGroupQuotaReservationSweeper(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        int configuredSweepSeconds = configuration.GetValue(
            "Quota:ReservationSweepSeconds",
            RequiredSweepSeconds);
        if (configuredSweepSeconds != RequiredSweepSeconds)
        {
            throw new InvalidOperationException(
                "Reservation sweep interval must equal thirty seconds.");
        }

        services.TryAddSingleton<ReservationSweeperProcessor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService,
                ReservationSweeperService>());
        return services;
    }
}
