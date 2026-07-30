namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountHealthWriter
{
    ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
        AccountHealthTransition transition,
        CancellationToken cancellationToken);
}
