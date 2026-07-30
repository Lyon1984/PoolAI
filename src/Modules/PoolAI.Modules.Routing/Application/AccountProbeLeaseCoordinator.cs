using System.Security.Cryptography;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Infrastructure;

namespace PoolAI.Modules.Routing.Application;

internal sealed class AccountProbeLeaseCoordinator(
    ICoordinationLeaseSet leases) : IAccountProbeLeaseCoordinator
{
    private readonly ICoordinationLeaseSet _leases =
        leases ?? throw new ArgumentNullException(nameof(leases));

    public async ValueTask<Result<IAccountProbeLease>> AcquireAsync(
        AccountProbeLeaseAcquireCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ConcurrencyLimit is <= 0 or > 10_000)
        {
            return Result.Failure<IAccountProbeLease>(
                "invalid_request",
                "The Account concurrency limit is outside the supported range.");
        }

        string owner = Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));
        CoordinationLeaseAcquireResult acquired = await _leases
            .AcquireAsync(
                new CoordinationLeaseAcquireRequest(
                    AccountRouter.LeaseKey(command.AccountId),
                    owner,
                    command.ConcurrencyLimit),
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired.Disposition == CoordinationLeaseAcquireDisposition.Unavailable)
        {
            return CoordinationUnavailable();
        }

        if (acquired.Disposition == CoordinationLeaseAcquireDisposition.CapacityExceeded)
        {
            return Result.Failure<IAccountProbeLease>(
                "account_capacity_unavailable",
                "The Account is at its concurrency limit.",
                RetrySeconds(acquired.RetryAfter));
        }

        if (acquired.Disposition is not (
            CoordinationLeaseAcquireDisposition.Acquired
            or CoordinationLeaseAcquireDisposition.Renewed))
        {
            return CoordinationUnavailable();
        }

        return Result.Success<IAccountProbeLease>(
            new AccountProbeLease(
                _leases,
                command.AccountId,
                owner,
                acquired.ExpiresAt));
    }

    private static long RetrySeconds(TimeSpan retryAfter) =>
        Math.Max(1, checked((long)Math.Ceiling(retryAfter.TotalSeconds)));

    private static Result<IAccountProbeLease> CoordinationUnavailable() =>
        Result.Failure<IAccountProbeLease>(
            "coordination_unavailable",
            "Redis coordination is temporarily unavailable.",
            retryAfterSeconds: 1);
}
