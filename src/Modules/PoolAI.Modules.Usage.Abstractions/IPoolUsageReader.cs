namespace PoolAI.Modules.Usage.Abstractions;

public interface IPoolUsageReader
{
    ValueTask<Result<PoolUsageSnapshot>> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken);
}
