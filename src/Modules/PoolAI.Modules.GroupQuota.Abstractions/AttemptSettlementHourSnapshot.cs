using System.Collections.ObjectModel;

namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed class AttemptSettlementHourSnapshot
{
    private readonly ReadOnlyCollection<AttemptSettlementFact> _facts;

    public AttemptSettlementHourSnapshot(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        IEnumerable<AttemptSettlementFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        DateTimeOffset utcBucketStart = bucketStart.ToUniversalTime();
        if (utcBucketStart.Minute != 0
            || utcBucketStart.Second != 0
            || utcBucketStart.Millisecond != 0
            || utcBucketStart.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentException(
                "The settlement snapshot bucket must start on an exact UTC hour.",
                nameof(bucketStart));
        }

        AttemptSettlementFact[] materialized = facts.ToArray();
        if (materialized.Length == 0
            || materialized.Any(fact =>
                fact.GroupId != groupId
                || fact.PeriodId != periodId
                || StartOfUtcHour(fact.CompletedAt) != utcBucketStart))
        {
            throw new ArgumentException(
                "Settlement snapshot facts must belong to the declared completion hour.",
                nameof(facts));
        }

        GroupId = groupId;
        PeriodId = periodId;
        BucketStart = utcBucketStart;
        _facts = Array.AsReadOnly(materialized);
    }

    public EntityId GroupId { get; }

    public EntityId PeriodId { get; }

    public DateTimeOffset BucketStart { get; }

    public IReadOnlyList<AttemptSettlementFact> Facts => _facts;

    private static DateTimeOffset StartOfUtcHour(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            minute: 0,
            second: 0,
            TimeSpan.Zero);
    }
}
