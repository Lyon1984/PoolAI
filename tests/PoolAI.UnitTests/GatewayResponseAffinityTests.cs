using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.UnitTests;

public sealed class GatewayResponseAffinityTests
{
    [Fact]
    public void ResponseHashesAreKeyGroupAndPepperScopedAndNeverExposeTheOriginalId()
    {
        byte[] pepper = Enumerable.Range(0, 32).Select(static index => (byte)index).ToArray();
        RecordingRecorder recorder = new();
        using GatewayResponseAffinity affinity = new(pepper, recorder);
        GatewayCanonicalAccess access = Access();
        string hash = affinity.Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, "resp_private");
        Assert.Matches("^[0-9a-f]{32}$", hash);
        Assert.Equal(hash, affinity.Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, "resp_private"));
        Assert.NotEqual(hash, affinity.Hash(EntityId.New(), access.ApiKey.ApiKeyId, "resp_private"));
        Assert.NotEqual(hash, affinity.Hash(access.Group.GroupId, EntityId.New(), "resp_private"));
        Assert.NotEqual(hash, affinity.Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, "resp_other"));
        Array.Clear(pepper);
        Assert.Equal(hash, affinity.Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, "resp_private"));
        NormalizedGatewayRequest request = Request(new { previous_response_id = "resp_private" });
        Assert.Equal(hash, affinity.ForRequest(access, request));
        foreach (object value in new object[] { new { }, new { previous_response_id = (string?)null },
            new { previous_response_id = 1 }, new { previous_response_id = "" }, new { previous_response_id = new string('x', 201) } })
        {
            Assert.Null(affinity.ForRequest(access, Request(value)));
        }
        Assert.Null(affinity.ForRequest(access, request with { Payload = default }));
    }

    [Fact]
    public async Task OnlySuccessfulSettledResponseIdsAreRecordedWithVersionFencesAndWritesStayAdvisory()
    {
        GatewayCanonicalAccess access = Access();
        AccountRoute route = new(access.Group.GroupId, EntityId.New(), EntityId.New(),
            AccountRouteProvider.OpenAi, "client", "upstream", new Uri("https://upstream.example"),
            new(true, true, true, true), DateTimeOffset.MaxValue, 7, 5, 3, 1);
        RecordingRecorder recorder = new();
        using GatewayResponseAffinity affinity = new(new byte[32], recorder);
        NormalizedUpstreamResult success = new(200, JsonSerializer.SerializeToElement(new { id = "resp_private" }), null, null);
        await affinity.RememberAsync(access, route, success, TestContext.Current.CancellationToken);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(access.Group.GroupId, recorder.GroupId);
        Assert.Equal(route.AccountId, recorder.AccountId);
        Assert.Equal(access.Group.Version, recorder.GroupVersion);
        Assert.Equal(route.SupplyConfigurationVersion, recorder.SupplyVersion);
        Assert.Equal(affinity.Hash(access.Group.GroupId, access.ApiKey.ApiKeyId, "resp_private"), recorder.Hash);
        foreach (var ignored in new NormalizedUpstreamResult?[] { null, success with { ErrorCode = "upstream_stream_error" },
            success with { Payload = default }, success with { Payload = JsonSerializer.SerializeToElement(new { }) },
            success with { Payload = JsonSerializer.SerializeToElement(new { id = 1 }) },
            success with { Payload = JsonSerializer.SerializeToElement(new { id = "" }) } })
        {
            await affinity.RememberAsync(access, route, ignored, TestContext.Current.CancellationToken);
        }
        Assert.Equal(1, recorder.Calls);
        recorder.Fail = true;
        await affinity.RememberAsync(access, route, success, TestContext.Current.CancellationToken);
        Assert.Equal(2, recorder.Calls);
    }

    private static GatewayCanonicalAccess Access()
    {
        EntityId user = EntityId.New();
        EntityId group = EntityId.New();
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        return new(new(EntityId.New(), user, group, true, [], 1, now),
            new(user, UserLifecycle.Active, SystemRole.User, 1, 1, now),
            new(EntityId.New(), user, group, "standard", now, now.AddDays(1), SubscriptionEffectiveStatus.Active, 1, now),
            new(group, GroupLifecycle.Active, 2, true, now, 6000));
    }

    private static NormalizedGatewayRequest Request(object value) => new(EntityId.New(), "client", false, JsonSerializer.SerializeToElement(value));

    private sealed class RecordingRecorder : IRouteAffinityRecorder
    {
        internal int Calls { get; private set; }
        internal bool Fail { get; set; }
        internal EntityId GroupId { get; private set; }
        internal EntityId AccountId { get; private set; }
        internal long GroupVersion { get; private set; }
        internal long SupplyVersion { get; private set; }
        internal string? Hash { get; private set; }
        public ValueTask StoreAsync(EntityId groupId, string sessionHash, EntityId accountId,
            long groupPolicyVersion, long supplyConfigurationVersion, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) { throw new IOException("fixture unavailable"); }
            GroupId = groupId; AccountId = accountId; GroupVersion = groupPolicyVersion;
            SupplyVersion = supplyConfigurationVersion; Hash = sessionHash;
            return ValueTask.CompletedTask;
        }
    }
}
