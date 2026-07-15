namespace PoolAI.Modules.Usage.Abstractions;

public interface IAccountUsageReader
{
    ValueTask<Result<AccountUsageSnapshot>> GetAsync(
        EntityId groupId,
        EntityId accountId,
        CancellationToken cancellationToken);
}
