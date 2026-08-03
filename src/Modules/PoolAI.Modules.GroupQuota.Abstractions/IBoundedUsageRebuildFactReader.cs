namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Reads one checkpoint-bounded immutable completion-hour snapshot without
/// exposing GroupQuota persistence.
/// </summary>
public interface IBoundedUsageRebuildFactReader
{
    ValueTask<BoundedUsageRebuildHourSnapshot> ReadHourAsync(
        EntityId groupId,
        EntityId periodId,
        DateTimeOffset bucketStart,
        long checkpointSourceSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
