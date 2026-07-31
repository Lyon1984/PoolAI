using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Routing.Infrastructure;

internal sealed class AccountActiveLeaseReader(
    ICoordinationLeaseCounter counter) : IAccountActiveLeaseReader
{
    private const int MaximumBatchSize = 100;
    private readonly ICoordinationLeaseCounter _counter =
        counter ?? throw new ArgumentNullException(nameof(counter));

    public async ValueTask<Result<IReadOnlyList<AccountActiveLeaseCount>>> ReadAsync(
        IReadOnlyList<EntityId> accountIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        if (accountIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<AccountActiveLeaseCount>>(
                Array.Empty<AccountActiveLeaseCount>());
        }

        if (accountIds.Count > MaximumBatchSize
            || accountIds.Any(static accountId => accountId.Value == Guid.Empty)
            || accountIds.Distinct().Count() != accountIds.Count)
        {
            return Unavailable();
        }

        CoordinationLeaseCountResult result = await _counter
            .CountActiveAsync(
                accountIds
                    .Select(AccountRouter.LeaseKey)
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Disposition != CoordinationLeaseCountDisposition.Counted
            || result.ActiveCounts.Count != accountIds.Count)
        {
            return Unavailable();
        }

        AccountActiveLeaseCount[] counts =
            new AccountActiveLeaseCount[accountIds.Count];
        for (int index = 0; index < accountIds.Count; index++)
        {
            int activeLeases = result.ActiveCounts[index];
            if (activeLeases is < 0 or > 10_000)
            {
                return Unavailable();
            }

            counts[index] = new AccountActiveLeaseCount(
                accountIds[index],
                activeLeases);
        }

        return Result.Success<IReadOnlyList<AccountActiveLeaseCount>>(counts);
    }

    private static Result<IReadOnlyList<AccountActiveLeaseCount>> Unavailable() =>
        Result.Failure<IReadOnlyList<AccountActiveLeaseCount>>(
            "coordination_unavailable",
            "Redis Account lease coordination is temporarily unavailable.",
            retryAfterSeconds: 1);
}
