namespace PoolAI.Modules.Operations.Application.Ports;

internal interface IOutboxReplayRepository
{
    ValueTask<OutboxReplayWriteResult> ReplayDeadAsync(
        OutboxReplayWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
