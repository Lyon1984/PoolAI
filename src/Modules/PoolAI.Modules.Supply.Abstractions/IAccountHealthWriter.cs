namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountHealthWriter
{
    ValueTask<Result<Unit>> RecordAsync(
        EntityId accountId,
        AccountHealth health,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}
