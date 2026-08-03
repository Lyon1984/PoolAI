using System.Globalization;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage.Infrastructure.Workers;

internal sealed record UsageProjectionRebuildWorkerOptions(
    bool Enabled,
    BoundedUsagePeriodRebuildRequest? Request)
{
    private const string Prefix = "WorkerJobs:UsageRebuild";
    private const string HourFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    internal static UsageProjectionRebuildWorkerOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue($"{Prefix}:Enabled", false))
        {
            return new UsageProjectionRebuildWorkerOptions(false, null);
        }

        EntityId groupId = ReadId(configuration, "GroupId");
        EntityId periodId = ReadId(configuration, "PeriodId");
        DateTimeOffset firstBucket = ReadHour(configuration, "FirstBucketStart");
        DateTimeOffset lastBucket = ReadHour(configuration, "LastBucketStart");
        if (lastBucket < firstBucket
            || (lastBucket - firstBucket).TotalHours + 1
                > UsagePeriodProjectionRebuilder.MaximumBucketCount)
        {
            throw new InvalidOperationException(
                "The one-shot Usage rebuild range is invalid or exceeds 744 hours.");
        }

        return new UsageProjectionRebuildWorkerOptions(
            true,
            new BoundedUsagePeriodRebuildRequest(
                groupId,
                periodId,
                firstBucket,
                lastBucket));
    }

    private static EntityId ReadId(IConfiguration configuration, string name)
    {
        string? value = configuration[$"{Prefix}:{name}"];
        if (!Guid.TryParseExact(value, "D", out Guid parsed) || parsed == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"The one-shot Usage rebuild {name} is invalid.");
        }

        return new EntityId(parsed);
    }

    private static DateTimeOffset ReadHour(
        IConfiguration configuration,
        string name)
    {
        string? value = configuration[$"{Prefix}:{name}"];
        if (!DateTimeOffset.TryParseExact(
                value,
                HourFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed)
            || parsed.Minute != 0
            || parsed.Second != 0)
        {
            throw new InvalidOperationException(
                $"The one-shot Usage rebuild {name} must be an exact UTC hour.");
        }

        return parsed;
    }

    public override string ToString() => nameof(UsageProjectionRebuildWorkerOptions);
}
