using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Endpoints;

namespace PoolAI.UnitTests;

public sealed class GroupQuotaHttpDefensiveTests
{
    [Fact]
    public void ActorParserSupportsUserAndRejectsUnknownRole()
    {
        EntityId userId = EntityId.New();
        DefaultHttpContext context = Context();
        context.User = Principal(userId, "user");

        Assert.True(GroupQuotaHttp.TryGetActor(context, out GroupActor? user));
        Assert.Equal(GroupControlRole.User, user!.Role);

        context.User = Principal(userId, "unknown");
        Assert.False(GroupQuotaHttp.TryGetActor(context, out GroupActor? unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void LifecycleConvertersCoverActiveAndRejectUnknownValues()
    {
        Assert.Equal(GroupLifecycle.Active, GroupQuotaHttp.ToLifecycle(GroupStatus.Active));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupQuotaHttp.ToLifecycle((GroupStatus)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupQuotaHttp.ToContractLifecycle((GroupLifecycle)999));
    }

    [Fact]
    public void InvalidUserTokenSetsBearerChallenge()
    {
        DefaultHttpContext context = Context();

        IResult result = GroupQuotaHttp.InvalidUserToken(context);

        Assert.NotNull(result);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public void InconsistentFrozenPresentationFailsClosed()
    {
        DefaultHttpContext context = Context();
        ResultError error = new(
            "resource_conflict",
            "conflict",
            Presentation: new ResultErrorPresentation(
                "different_code",
                StatusCodes.Status409Conflict,
                "Conflict",
                "Conflict",
                Retryable: false));

        Assert.Throws<InvalidOperationException>(() =>
            GroupQuotaHttp.FromError(context, error));
    }

    private static DefaultHttpContext Context() => new()
    {
        TraceIdentifier = EntityId.New().Value.ToString("D"),
    };

    private static ClaimsPrincipal Principal(EntityId userId, string role) => new(
        new ClaimsIdentity(
        [
            new Claim("sub", userId.Value.ToString("D")),
            new Claim("role", role),
            new Claim("token_version", "1"),
        ],
        authenticationType: "unit-test"));
}
