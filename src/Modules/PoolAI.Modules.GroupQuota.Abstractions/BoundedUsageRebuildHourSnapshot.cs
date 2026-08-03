using System.Collections.ObjectModel;

namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Immutable facts for one exact UTC completion hour as visible at one logical
/// GroupQuota checkpoint. An empty fact set is a valid snapshot.
/// </summary>
public sealed class BoundedUsageRebuildHourSnapshot
{
    private readonly ReadOnlyCollection<AttemptSettlementFact> _facts;

    public BoundedUsageRebuildHourSnapshot(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        long checkpointSourceSequence,
        IEnumerable<AttemptSettlementFact> facts)
    {
        ValidateId(groupId, nameof(groupId));
        ValidateId(periodId, nameof(periodId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointSourceSequence);
        ArgumentNullException.ThrowIfNull(facts);
        if (!IsExactUtcHour(bucketStart))
        {
            throw new ArgumentException(
                "The bounded rebuild bucket must start on an exact UTC hour.",
                nameof(bucketStart));
        }

        AttemptSettlementFact[] materialized = facts.ToArray();
        HashSet<EntityId> attemptIds = [];
        if (materialized.Any(fact =>
                fact is null
                || fact.GroupId != groupId
                || fact.PeriodId != periodId
                || StartOfUtcHour(fact.CompletedAt) != bucketStart
                || !attemptIds.Add(fact.AttemptId)))
        {
            throw new ArgumentException(
                "Bounded rebuild facts must be unique and belong to the declared completion hour.",
                nameof(facts));
        }

        GroupId = groupId;
        PeriodId = periodId;
        BucketStart = bucketStart;
        CheckpointSourceSequence = checkpointSourceSequence;
        _facts = Array.AsReadOnly(materialized);
    }

    public EntityId GroupId { get; }

    public EntityId PeriodId { get; }

    public DateTimeOffset BucketStart { get; }

    public long CheckpointSourceSequence { get; }

    public IReadOnlyList<AttemptSettlementFact> Facts => _facts;

    private static bool IsExactUtcHour(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
        && value.Minute == 0
        && value.Second == 0
        && value.Ticks % TimeSpan.TicksPerSecond == 0;

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

    private static void ValidateId(EntityId entityId, string parameterName)
    {
        if (entityId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The entity identifier must be a non-empty UUID.",
                parameterName);
        }
    }
}
