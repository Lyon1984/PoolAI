namespace PoolAI.Modules.Operations.Abstractions;

public interface IOutboxAppender
{
    ValueTask AppendAsync(
        IntegrationEvent integrationEvent,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
