namespace PoolAI.Modules.Operations.Application;

public interface IReplayDeadOutboxUseCase
{
    ValueTask<Result<OutboxReplayOutcome>> ExecuteAsync(
        ReplayDeadOutboxCommand command,
        CancellationToken cancellationToken);
}
