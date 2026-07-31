namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountProbeLease : IAsyncDisposable
{
    EntityId AccountId { get; }

    DateTimeOffset ExpiresAt { get; }

    ValueTask<Result<DateTimeOffset>> RenewAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<bool>> ReleaseAsync(
        CancellationToken cancellationToken);
}
