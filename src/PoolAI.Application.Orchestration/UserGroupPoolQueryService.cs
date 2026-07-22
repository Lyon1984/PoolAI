using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed class UserGroupPoolQueryService(
    IUserSubscriptionGrantReader subscriptionGrantReader,
    IGroupPoolSummaryReader groupPoolSummaryReader) : IListUserGroupPoolsUseCase
{
    private readonly IUserSubscriptionGrantReader _subscriptionGrantReader =
        subscriptionGrantReader ?? throw new ArgumentNullException(nameof(subscriptionGrantReader));
    private readonly IGroupPoolSummaryReader _groupPoolSummaryReader =
        groupPoolSummaryReader ?? throw new ArgumentNullException(nameof(groupPoolSummaryReader));

    public async ValueTask<Result<IReadOnlyList<UserGroupPoolView>>> ExecuteAsync(
        ListUserGroupPoolsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        Result<IReadOnlyList<UserSubscriptionGrantSnapshot>> grantsResult =
            await _subscriptionGrantReader.ListActiveAsync(
                query.UserId,
                cancellationToken).ConfigureAwait(false);
        if (grantsResult.IsFailure)
        {
            return Propagate<IReadOnlyList<UserGroupPoolView>>(grantsResult.Error);
        }

        UserSubscriptionGrantSnapshot[] grants = grantsResult.Value
            .GroupBy(static grant => grant.GroupId)
            .Select(static candidates => candidates
                .OrderByDescending(static grant => grant.UpdatedAt)
                .ThenBy(static grant => grant.SubscriptionId.Value)
                .First())
            .OrderBy(static grant => grant.GroupId.Value)
            .ToArray();
        if (grants.Length == 0)
        {
            return Result.Success<IReadOnlyList<UserGroupPoolView>>(
                Array.Empty<UserGroupPoolView>());
        }

        Result<IReadOnlyList<GroupPoolSummarySnapshot>> poolsResult =
            await _groupPoolSummaryReader.GetByGroupIdsAsync(
                grants.Select(static grant => grant.GroupId).ToArray(),
                cancellationToken).ConfigureAwait(false);
        if (poolsResult.IsFailure)
        {
            return Propagate<IReadOnlyList<UserGroupPoolView>>(poolsResult.Error);
        }

        Dictionary<EntityId, GroupPoolSummarySnapshot> pools = poolsResult.Value
            .GroupBy(static pool => pool.GroupId)
            .ToDictionary(static group => group.Key, static group => group.Single());
        if (grants.Any(grant => !pools.ContainsKey(grant.GroupId)))
        {
            return Result.Failure<IReadOnlyList<UserGroupPoolView>>(
                "dependency_unavailable",
                "A canonical Group pool projection is unavailable.",
                retryAfterSeconds: 1);
        }

        IReadOnlyList<UserGroupPoolView> views = grants
            .Select(grant => ToView(grant, pools[grant.GroupId]))
            .ToArray();
        return Result.Success(views);
    }

    private static UserGroupPoolView ToView(
        UserSubscriptionGrantSnapshot grant,
        GroupPoolSummarySnapshot pool)
    {
        BigInteger remaining = BigInteger.Max(
            BigInteger.Zero,
            pool.TotalTokens - pool.ConsumedTokens - pool.ReservedTokens);
        return new UserGroupPoolView(
            pool.GroupId,
            pool.GroupName,
            grant.SubscriptionId,
            grant.PlanName,
            grant.ExpiresAt,
            QuotaStatus(pool.QuotaStatus),
            pool.TotalTokens,
            pool.ConsumedTokens,
            pool.ReservedTokens,
            remaining,
            grant.UpdatedAt > pool.UpdatedAt ? grant.UpdatedAt : pool.UpdatedAt);
    }

    private static string QuotaStatus(GroupPoolQuotaStatus status) => status switch
    {
        GroupPoolQuotaStatus.Active => "active",
        GroupPoolQuotaStatus.Exhausted => "exhausted",
        GroupPoolQuotaStatus.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static Result<T> Propagate<T>(ResultError error) => Result.Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);
}
