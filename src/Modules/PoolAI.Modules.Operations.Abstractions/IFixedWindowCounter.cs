namespace PoolAI.Modules.Operations.Abstractions;

public interface IFixedWindowCounter
{
    ValueTask<FixedWindowCounterResult> IncrementAsync(
        FixedWindowCounterRequest request,
        CancellationToken cancellationToken);
}
