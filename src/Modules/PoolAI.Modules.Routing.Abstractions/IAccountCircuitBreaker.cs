namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountCircuitBreaker
{
    ValueTask<Result<AccountBreakerSnapshot>> ReadAsync(
        EntityId accountId,
        CancellationToken cancellationToken);

    ValueTask<Result<AccountBreakerSnapshot>> RecordAsync(
        AccountBreakerRecordCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<AccountBreakerProbeAcquireResult>> TryAcquireProbeAsync(
        EntityId accountId,
        CancellationToken cancellationToken);
}
