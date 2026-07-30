using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.Modules.Routing.Infrastructure;

internal sealed class AccountProbeLease(
    ICoordinationLeaseSet leases,
    EntityId accountId,
    string owner,
    DateTimeOffset expiresAt) : IAccountProbeLease
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICoordinationLeaseSet _leases =
        leases ?? throw new ArgumentNullException(nameof(leases));
    private readonly string _owner =
        owner ?? throw new ArgumentNullException(nameof(owner));
    private DateTimeOffset _expiresAt = expiresAt;
    private bool _released;

    public EntityId AccountId { get; } = accountId;

    public DateTimeOffset ExpiresAt => _expiresAt;

    public async ValueTask<Result<DateTimeOffset>> RenewAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_released)
            {
                return LeaseLost<DateTimeOffset>();
            }

            CoordinationLeaseRenewResult renewed = await _leases
                .RenewAsync(
                    new CoordinationLeaseOwner(
                        AccountRouter.LeaseKey(AccountId),
                        _owner),
                    cancellationToken)
                .ConfigureAwait(false);
            if (renewed.Disposition == CoordinationLeaseRenewDisposition.Unavailable)
            {
                return CoordinationUnavailable<DateTimeOffset>();
            }

            if (renewed.Disposition == CoordinationLeaseRenewDisposition.Lost)
            {
                _released = true;
                return LeaseLost<DateTimeOffset>();
            }

            if (renewed.Disposition != CoordinationLeaseRenewDisposition.Renewed)
            {
                return CoordinationUnavailable<DateTimeOffset>();
            }

            _expiresAt = renewed.ExpiresAt;
            return Result.Success(_expiresAt);
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
                        AccountRouter.LeaseKey(AccountId),
                        _owner),
                    cancellationToken)
                .ConfigureAwait(false);
            if (released == CoordinationLeaseReleaseResult.Unavailable)
            {
                return CoordinationUnavailable<bool>();
            }

            _released = true;
            return Result.Success(
                released == CoordinationLeaseReleaseResult.Released);
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
            // The Redis TTL remains the final cleanup boundary.
        }
    }

    private static Result<T> LeaseLost<T>() =>
        Result.Failure<T>(
            "account_capacity_unavailable",
            "The Account probe lease is no longer owned.",
            retryAfterSeconds: 1);

    private static Result<T> CoordinationUnavailable<T>() =>
        Result.Failure<T>(
            "coordination_unavailable",
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);
}
