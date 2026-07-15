namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupStatusReader
{
    ValueTask<Result<GroupSnapshot>> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken);
}
