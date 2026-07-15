namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountLease : IAsyncDisposable
{
    AccountRoute Route { get; }

    ValueTask<Result<AccountRoute>> RenewAsync(CancellationToken cancellationToken);
}
