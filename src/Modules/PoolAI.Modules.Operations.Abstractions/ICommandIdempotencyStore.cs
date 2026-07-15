namespace PoolAI.Modules.Operations.Abstractions;

public interface ICommandIdempotencyStore
{
    ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        CommandIdempotencyRequest request,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> HeartbeatAsync(
        CommandIdempotencyHeartbeat heartbeat,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<bool> CompleteAsync(
        CommandIdempotencyCompletion completion,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
