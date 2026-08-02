using System.Collections.ObjectModel;

namespace PoolAI.Modules.Usage.Application;

internal sealed class UsageHourProjection
{
    private readonly ReadOnlyCollection<AccountUsageHourProjection> _accounts;

    internal UsageHourProjection(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        UsageHourlyAggregate group,
        IEnumerable<AccountUsageHourProjection> accounts)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(accounts);
        GroupId = groupId;
        PeriodId = periodId;
        BucketStart = bucketStart.ToUniversalTime();
        Group = group;
        _accounts = Array.AsReadOnly(accounts.ToArray());
    }

    internal EntityId GroupId { get; }

    internal EntityId PeriodId { get; }

    internal DateTimeOffset BucketStart { get; }

    internal UsageHourlyAggregate Group { get; }

    internal IReadOnlyList<AccountUsageHourProjection> Accounts => _accounts;
}
