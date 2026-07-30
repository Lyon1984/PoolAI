namespace PoolAI.Modules.Operations.Abstractions;

public interface ICoordinationValueStore
{
    ValueTask<CoordinationValueReadResult> GetAndRefreshAsync(
        string keyBase,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);

    ValueTask<CoordinationValueWriteResult> SetAsync(
        string keyBase,
        string value,
        TimeSpan timeToLive,
        CancellationToken cancellationToken);
}
