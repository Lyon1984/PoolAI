namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupPoolSummaryReader
{
    ValueTask<Result<IReadOnlyList<GroupPoolSummarySnapshot>>> GetByGroupIdsAsync(
        IReadOnlyCollection<EntityId> groupIds,
        CancellationToken cancellationToken);
}
