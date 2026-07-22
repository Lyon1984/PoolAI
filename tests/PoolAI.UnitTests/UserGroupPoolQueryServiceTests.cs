using System.Numerics;
using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.UnitTests;

public sealed class UserGroupPoolQueryServiceTests
{
    private static readonly EntityId UserId = Id("10000000-0000-0000-0000-000000000001");
    private static readonly EntityId GroupOne = Id("20000000-0000-0000-0000-000000000001");
    private static readonly EntityId GroupTwo = Id("20000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task NoActiveGrantReturnsEmptyWithoutReadingQuota()
    {
        FakeGrantReader grants = new([]);
        FakePoolReader pools = new([]);
        UserGroupPoolQueryService service = new(grants, pools);

        Result<IReadOnlyList<UserGroupPoolView>> result = await service.ExecuteAsync(
            new ListUserGroupPoolsQuery(UserId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
        Assert.Equal(0, pools.Calls);
    }

    [Fact]
    public async Task ActiveGrantsAreDeduplicatedAndJoinedWithCanonicalGroupQuota()
    {
        DateTimeOffset now = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);
        FakeGrantReader grants = new(
        [
            Grant(GroupOne, "old-plan", now.AddDays(1), now.AddMinutes(-2), 1),
            Grant(GroupOne, "current-plan", now.AddDays(2), now, 2),
            Grant(GroupTwo, "disabled-plan", now.AddDays(3), now.AddMinutes(-1), 3),
        ]);
        FakePoolReader pools = new(
        [
            Pool(GroupTwo, "Group two", 500, 40, 10, GroupPoolQuotaStatus.Disabled, now),
            Pool(GroupOne, "Group one", 100, 60, 50, GroupPoolQuotaStatus.Active, now.AddMinutes(1)),
        ]);
        UserGroupPoolQueryService service = new(grants, pools);

        Result<IReadOnlyList<UserGroupPoolView>> result = await service.ExecuteAsync(
            new ListUserGroupPoolsQuery(UserId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal([GroupOne, GroupTwo], pools.RequestedGroupIds);
        Assert.Collection(
            result.Value,
            first =>
            {
                Assert.Equal(GroupOne, first.GroupId);
                Assert.Equal("current-plan", first.PlanName);
                Assert.Equal("active", first.QuotaStatus);
                Assert.Equal(BigInteger.Zero, first.RemainingTokens);
                Assert.Equal(now.AddMinutes(1), first.UpdatedAt);
            },
            second =>
            {
                Assert.Equal(GroupTwo, second.GroupId);
                Assert.Equal("disabled-plan", second.PlanName);
                Assert.Equal("disabled", second.QuotaStatus);
                Assert.Equal(new BigInteger(450), second.RemainingTokens);
                Assert.Equal(now, second.UpdatedAt);
            });
    }

    [Fact]
    public async Task MissingCanonicalQuotaFailsClosed()
    {
        DateTimeOffset now = new(2026, 7, 17, 8, 0, 0, TimeSpan.Zero);
        UserGroupPoolQueryService service = new(
            new FakeGrantReader([Grant(GroupOne, "plan", now.AddDays(1), now, 1)]),
            new FakePoolReader([]));

        Result<IReadOnlyList<UserGroupPoolView>> result = await service.ExecuteAsync(
            new ListUserGroupPoolsQuery(UserId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public async Task GrantReadFailureIsPropagatedWithoutReadingQuota()
    {
        FakeGrantReader grants = new([], Result.Failure<IReadOnlyList<UserSubscriptionGrantSnapshot>>(
            "coordination_unavailable",
            "synthetic",
            retryAfterSeconds: 1));
        FakePoolReader pools = new([]);
        UserGroupPoolQueryService service = new(grants, pools);

        Result<IReadOnlyList<UserGroupPoolView>> result = await service.ExecuteAsync(
            new ListUserGroupPoolsQuery(UserId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(0, pools.Calls);
    }

    private static UserSubscriptionGrantSnapshot Grant(
        EntityId groupId,
        string planName,
        DateTimeOffset expiresAt,
        DateTimeOffset updatedAt,
        int discriminator) => new(
            Id($"30000000-0000-0000-0000-{discriminator:000000000000}"),
            UserId,
            groupId,
            planName,
            expiresAt,
            updatedAt);

    private static GroupPoolSummarySnapshot Pool(
        EntityId groupId,
        string name,
        int total,
        int consumed,
        int reserved,
        GroupPoolQuotaStatus status,
        DateTimeOffset updatedAt) => new(
            groupId,
            name,
            GroupLifecycle.Active,
            new BigInteger(total),
            new BigInteger(consumed),
            new BigInteger(reserved),
            status,
            updatedAt);

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed class FakeGrantReader : IUserSubscriptionGrantReader
    {
        private readonly Result<IReadOnlyList<UserSubscriptionGrantSnapshot>> _result;

        internal FakeGrantReader(
            IReadOnlyList<UserSubscriptionGrantSnapshot> values,
            Result<IReadOnlyList<UserSubscriptionGrantSnapshot>>? result = null)
        {
            _result = result ?? Result.Success(values);
        }

        public ValueTask<Result<IReadOnlyList<UserSubscriptionGrantSnapshot>>> ListActiveAsync(
            EntityId userId,
            CancellationToken cancellationToken) => ValueTask.FromResult(_result);
    }

    private sealed class FakePoolReader(
        IReadOnlyList<GroupPoolSummarySnapshot> values) : IGroupPoolSummaryReader
    {
        internal int Calls { get; private set; }

        internal IReadOnlyCollection<EntityId> RequestedGroupIds { get; private set; } = [];

        public ValueTask<Result<IReadOnlyList<GroupPoolSummarySnapshot>>> GetByGroupIdsAsync(
            IReadOnlyCollection<EntityId> groupIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            RequestedGroupIds = groupIds;
            return ValueTask.FromResult(Result.Success(values));
        }
    }
}
