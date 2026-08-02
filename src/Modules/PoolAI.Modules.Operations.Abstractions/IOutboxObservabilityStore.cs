namespace PoolAI.Modules.Operations.Abstractions;

public interface IOutboxObservabilityStore
{
    ValueTask<OutboxObservabilitySnapshot> ReadAsync(
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
