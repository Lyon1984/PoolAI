namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountBreakerProbe : IAsyncDisposable
{
    EntityId AccountId { get; }

    DateTimeOffset ExpiresAt { get; }

    ValueTask<Result<AccountBreakerSnapshot>> CompleteAsync(
        AccountBreakerProbeCompletion completion,
        CancellationToken cancellationToken);
}
