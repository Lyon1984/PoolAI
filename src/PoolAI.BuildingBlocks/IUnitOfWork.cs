namespace PoolAI.BuildingBlocks;

public interface IUnitOfWork : IAsyncDisposable
{
    IUnitOfWorkContext Context { get; }

    ValueTask CommitAsync(CancellationToken cancellationToken);
}
