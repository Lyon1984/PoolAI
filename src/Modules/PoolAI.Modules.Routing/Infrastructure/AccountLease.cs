using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.Modules.Routing.Infrastructure;

internal sealed class AccountLease(
    ICoordinationLeaseSet leases,
    AccountRoute route,
    string owner) : IAccountLease
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICoordinationLeaseSet _leases =
        leases ?? throw new ArgumentNullException(nameof(leases));
    private readonly string _owner =
        owner ?? throw new ArgumentNullException(nameof(owner));
    private AccountRoute _route =
        route ?? throw new ArgumentNullException(nameof(route));
    private bool _released;

    public AccountRoute Route => _route;

    public async ValueTask<AccountLeaseRenewResult> RenewAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_released)
            {
                return AccountLeaseRenewResult.Lost;
            }

            CoordinationLeaseRenewResult renewed = await _leases
                .RenewAsync(
                    new CoordinationLeaseOwner(
                        AccountRouter.LeaseKey(_route.AccountId),
                        _owner),
                    cancellationToken)
                .ConfigureAwait(false);
            if (renewed.Disposition == CoordinationLeaseRenewDisposition.Unavailable)
            {
                return AccountLeaseRenewResult.Unavailable;
            }

            if (renewed.Disposition == CoordinationLeaseRenewDisposition.Lost)
            {
                _released = true;
                return AccountLeaseRenewResult.Lost;
            }

            if (renewed.Disposition != CoordinationLeaseRenewDisposition.Renewed)
            {
                return AccountLeaseRenewResult.Unavailable;
            }

            _route = _route with { LeaseExpiresAt = renewed.ExpiresAt };
            return AccountLeaseRenewResult.Renewed(_route);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Result<bool>> ReleaseAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_released)
            {
                return Result.Success(false);
            }

            CoordinationLeaseReleaseResult released = await _leases
                .ReleaseAsync(
                    new CoordinationLeaseOwner(
                        AccountRouter.LeaseKey(_route.AccountId),
                        _owner),
                    cancellationToken)
                .ConfigureAwait(false);
            if (released == CoordinationLeaseReleaseResult.Unavailable)
            {
                return CoordinationUnavailable<bool>();
            }

            _released = true;
            return Result.Success(released == CoordinationLeaseReleaseResult.Released);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _ = await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposal is idempotent and a lease naturally expires in Redis.
        }
    }

    private static Result<T> CoordinationUnavailable<T>() =>
        Result.Failure<T>(
            "coordination_unavailable",
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);
}
