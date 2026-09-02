using System.Net;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.UnitTests;

public sealed class GatewayCanonicalAdmissionServiceTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        2,
        0,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ReadsCanonicalAccessInFrozenOrder()
    {
        AdmissionHarness harness = new();

        Result<GatewayCanonicalAccess> result = await harness.Service
            .AuthorizeAsync(
                "secret",
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["api-key", "user", "subscription", "group"],
            harness.Calls);
        Assert.Equal(harness.GroupId, result.Value.Group.GroupId);
        Assert.Equal(6_000, result.Value.Group.RequestsPerMinute);
    }

    [Fact]
    public async Task CidrMismatchIsInvalidApiKeyAndStopsCanonicalReads()
    {
        AdmissionHarness harness = new(allowedCidrs: ["192.0.2.0/24"]);

        Result<GatewayCanonicalAccess> result = await harness.Service
            .AuthorizeAsync(
                "secret",
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_api_key", result.Error.Code);
        Assert.Equal(["api-key"], harness.Calls);
    }

    [Fact]
    public async Task DisabledUserStopsBeforeSubscriptionAndGroup()
    {
        AdmissionHarness harness = new(userLifecycle: UserLifecycle.Disabled);

        Result<GatewayCanonicalAccess> result = await harness.Service
            .AuthorizeAsync(
                "secret",
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("user_disabled", result.Error.Code);
        Assert.Equal(["api-key", "user"], harness.Calls);
    }

    [Fact]
    public async Task MissingSubscriptionStopsBeforeGroup()
    {
        AdmissionHarness harness = new(subscriptionExists: false);

        Result<GatewayCanonicalAccess> result = await harness.Service
            .AuthorizeAsync(
                "secret",
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("subscription_required", result.Error.Code);
        Assert.Equal(["api-key", "user", "subscription"], harness.Calls);
    }

    [Fact]
    public async Task DisabledGroupReturnsFrozenAdmissionError()
    {
        AdmissionHarness harness = new(groupLifecycle: GroupLifecycle.Disabled);

        Result<GatewayCanonicalAccess> result = await harness.Service
            .AuthorizeAsync(
                "secret",
                IPAddress.Loopback,
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        Assert.True(result.IsFailure);
        Assert.Equal("group_disabled", result.Error.Code);
        Assert.Equal(
            ["api-key", "user", "subscription", "group"],
            harness.Calls);
    }

    private sealed class AdmissionHarness
    {
        internal AdmissionHarness(
            IReadOnlyList<string>? allowedCidrs = null,
            UserLifecycle userLifecycle = UserLifecycle.Active,
            bool subscriptionExists = true,
            GroupLifecycle groupLifecycle = GroupLifecycle.Active)
        {
            UserId = EntityId.New();
            GroupId = EntityId.New();
            ApiKeyAccessSnapshot apiKey = new(
                EntityId.New(),
                UserId,
                GroupId,
                IsEffective: true,
                allowedCidrs ?? [],
                Version: 2,
                ObservedAt: Now);
            UserStatusSnapshot user = new(
                UserId,
                userLifecycle,
                SystemRole.User,
                TokenVersion: 3,
                Version: 4,
                ObservedAt: Now);
            Result<SubscriptionAccessSnapshot> subscription =
                subscriptionExists
                    ? Result.Success(new SubscriptionAccessSnapshot(
                        EntityId.New(),
                        UserId,
                        GroupId,
                        "standard",
                        Now.AddDays(-1),
                        Now.AddDays(1),
                        SubscriptionEffectiveStatus.Active,
                        Version: 5,
                        ObservedAt: Now))
                    : Result.Failure<SubscriptionAccessSnapshot>(
                        "subscription_required",
                        "No subscription exists.");
            GroupSnapshot group = new(
                GroupId,
                groupLifecycle,
                Version: 6,
                HasCurrentQuotaPeriod: true,
                ObservedAt: Now,
                RequestsPerMinute: 6_000);

            Service = new GatewayCanonicalAdmissionService(
                new FakeApiKeyReader(Calls, apiKey),
                new FakeUserReader(Calls, user),
                new FakeSubscriptionReader(Calls, subscription),
                new FakeGroupReader(Calls, group),
                new GatewayClientIpResolver(new GatewayIngressOptions()));
        }

        internal List<string> Calls { get; } = [];

        internal EntityId UserId { get; }

        internal EntityId GroupId { get; }

        internal GatewayCanonicalAdmissionService Service { get; }
    }

    private sealed class FakeApiKeyReader(
        List<string> calls,
        ApiKeyAccessSnapshot value) : IApiKeyAuthenticator
    {
        public ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
            string presentedKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("secret", presentedKey);
            calls.Add("api-key");
            return ValueTask.FromResult(Result.Success(value));
        }
    }

    private sealed class FakeUserReader(
        List<string> calls,
        UserStatusSnapshot value) : IUserStatusReader
    {
        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(value.UserId, userId);
            calls.Add("user");
            return ValueTask.FromResult(Result.Success(value));
        }
    }

    private sealed class FakeSubscriptionReader(
        List<string> calls,
        Result<SubscriptionAccessSnapshot> value) : ISubscriptionAccessReader
    {
        public ValueTask<Result<SubscriptionAccessSnapshot>>
            GetEffectiveAccessAsync(
                EntityId userId,
                EntityId groupId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("subscription");
            return ValueTask.FromResult(value);
        }
    }

    private sealed class FakeGroupReader(
        List<string> calls,
        GroupSnapshot value) : IGroupStatusReader
    {
        public ValueTask<Result<GroupSnapshot>> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(value.GroupId, groupId);
            calls.Add("group");
            return ValueTask.FromResult(Result.Success(value));
        }
    }
}
