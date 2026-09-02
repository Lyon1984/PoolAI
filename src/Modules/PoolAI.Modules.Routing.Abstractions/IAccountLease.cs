namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountLease : IAsyncDisposable
{
    AccountRoute Route { get; }

    ValueTask<AccountLeaseRenewResult> RenewAsync(
        CancellationToken cancellationToken);

    ValueTask<Result<bool>> ReleaseAsync(CancellationToken cancellationToken);
}
