namespace PoolAI.BuildingBlocks;

public interface IUnitOfWorkFactory
{
    ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken);
}
