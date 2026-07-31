namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountActiveLeaseReader
{
    ValueTask<Result<IReadOnlyList<AccountActiveLeaseCount>>> ReadAsync(
        IReadOnlyList<EntityId> accountIds,
        CancellationToken cancellationToken);
}
