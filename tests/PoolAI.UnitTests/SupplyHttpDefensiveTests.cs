#pragma warning disable MA0051 // Defensive protocol matrices intentionally keep related branches together.
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Endpoints;

namespace PoolAI.UnitTests;

public sealed class SupplyHttpDefensiveTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634190");
    private static readonly Guid UserId = Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634191");

    [Theory]
    [InlineData("admin")]
    [InlineData("operator")]
    [InlineData("auditor")]
    [InlineData("user")]
    public void ActorClaimsAcceptEveryFrozenRoleAndFrameworkFallback(string role)
    {
        DefaultHttpContext context = Context();
        context.User = Principal(
            new Claim("sub", UserId.ToString("D")),
            new Claim("role", role),
            new Claim("token_version", "7"));

        Assert.True(SupplyHttp.TryGetActor(
            context,
            out AuthenticatedSupplyActor? actor));
        Assert.Equal(new EntityId(UserId), actor!.UserId);
        Assert.Equal(role, actor.Role);
        Assert.Equal(7, actor.TokenVersion);
        Assert.Equal(actor, SupplyHttp.RequireActor(context));

        DefaultHttpContext fallback = Context();
        fallback.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new Claim(ClaimTypes.Role, role),
            new Claim("token_version", "1"));
        Assert.True(SupplyHttp.TryGetActor(fallback, out _));
    }

    [Fact]
    public void ActorClaimsAndRequestIdentifiersFailClosed()
    {
        ClaimsPrincipal[] invalid =
        [
            Principal(
                new Claim("role", "admin"),
                new Claim("token_version", "1")),
            Principal(
                new Claim("sub", Guid.Empty.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "1")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "owner"),
                new Claim("token_version", "1")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "not-a-number")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "0")),
        ];

        foreach (ClaimsPrincipal principal in invalid)
        {
            DefaultHttpContext context = Context();
            context.User = principal;
            Assert.False(SupplyHttp.TryGetActor(context, out _));
            Assert.Throws<InvalidOperationException>(() =>
                SupplyHttp.RequireActor(context));
        }

        Assert.Equal(new EntityId(RequestId), SupplyHttp.RequestId(Context()));
        DefaultHttpContext invalidRequestId = Context();
        invalidRequestId.TraceIdentifier = "not-a-uuid";
        Assert.Throws<InvalidOperationException>(() =>
            SupplyHttp.RequestId(invalidRequestId));
    }

    [Fact]
    public void EntityPaginationAndConditionalInputsCoverAllCanonicalForms()
    {
        DefaultHttpContext context = Context();
        Assert.False(SupplyHttp.TryGetEntityId(
            context,
            Guid.Empty,
            "/accountId",
            "Account",
            out EntityId empty,
            out IResult? failure));
        Assert.Equal(default, empty);
        AssertStatus(failure, StatusCodes.Status400BadRequest);
        Assert.True(SupplyHttp.TryGetEntityId(
            context,
            UserId,
            "/accountId",
            "Account",
            out EntityId entityId,
            out failure));
        Assert.Equal(new EntityId(UserId), entityId);
        Assert.Null(failure);

        Assert.True(SupplyHttp.TryGetPagination(
            context,
            null,
            out int limit,
            out failure));
        Assert.Equal(50, limit);
        foreach (string invalid in new[] { "", "0", "101", "-1", "1.5" })
        {
            Assert.False(SupplyHttp.TryGetPagination(
                Context(),
                invalid,
                out _,
                out failure));
            AssertStatus(failure, StatusCodes.Status400BadRequest);
        }
        Assert.True(SupplyHttp.TryGetPagination(
            Context(),
            "100",
            out limit,
            out failure));
        Assert.Equal(100, limit);
        Assert.Null(failure);

        Assert.False(SupplyHttp.TryGetIdempotencyKey(
            Context(),
            out _,
            out failure));
        AssertStatus(failure, StatusCodes.Status428PreconditionRequired);
        foreach (string invalid in new[] { new string('x', 129), "bad\tkey", "bad key" })
        {
            DefaultHttpContext invalidKey = Context();
            invalidKey.Request.Headers["Idempotency-Key"] = invalid;
            Assert.False(SupplyHttp.TryGetIdempotencyKey(
                invalidKey,
                out _,
                out failure));
            AssertStatus(failure, StatusCodes.Status400BadRequest);
        }
        DefaultHttpContext validKey = Context();
        validKey.Request.Headers["Idempotency-Key"] = "visible-key";
        Assert.True(SupplyHttp.TryGetIdempotencyKey(
            validKey,
            out string? idempotencyKey,
            out failure));
        Assert.Equal("visible-key", idempotencyKey);
        Assert.Null(failure);

        Assert.False(SupplyHttp.TryGetExpectedVersion(
            Context(),
            out long version,
            out failure));
        Assert.Equal(0, version);
        AssertStatus(failure, StatusCodes.Status428PreconditionRequired);
        foreach (string invalid in new[]
                 {
                     "v1",
                     "W/\"v1\"",
                     "\"v0\"",
                     "\"vX\"",
                     "\"v1",
                     "\"v9223372036854775808\"",
                 })
        {
            DefaultHttpContext invalidVersion = Context();
            invalidVersion.Request.Headers.IfMatch = invalid;
            Assert.False(SupplyHttp.TryGetExpectedVersion(
                invalidVersion,
                out version,
                out failure));
            Assert.Equal(0, version);
            AssertStatus(failure, StatusCodes.Status400BadRequest);
        }
        DefaultHttpContext validVersion = Context();
        validVersion.Request.Headers.IfMatch = "\"v42\"";
        Assert.True(SupplyHttp.TryGetExpectedVersion(
            validVersion,
            out version,
            out failure));
        Assert.Equal(42, version);
        Assert.Null(failure);
    }

    [Fact]
    public void ChangeReasonContentTypeAndMetadataRemainBounded()
    {
        foreach (string invalid in new[]
                 {
                     "",
                     " ",
                     new string('r', 501),
                     "line\rbreak",
                     "line\nbreak",
                 })
        {
            DefaultHttpContext context = Context();
            context.Request.Headers["X-Change-Reason"] = invalid;
            Assert.False(SupplyHttp.TryGetChangeReason(
                context,
                out _,
                out IResult? failure));
            AssertStatus(failure, StatusCodes.Status400BadRequest);
        }

        DefaultHttpContext valid = Context();
        valid.Request.Headers["X-Change-Reason"] = "reviewed change";
        Assert.True(SupplyHttp.TryGetChangeReason(
            valid,
            out string? reason,
            out IResult? reasonFailure));
        Assert.Equal("reviewed change", reason);
        Assert.Null(reasonFailure);

        Assert.NotNull(SupplyHttp.RequireContentType(Context(), "application/json"));
        DefaultHttpContext different = Context();
        different.Request.ContentType = "text/plain";
        Assert.NotNull(SupplyHttp.RequireContentType(different, "application/json"));
        DefaultHttpContext parameterized = Context();
        parameterized.Request.ContentType = "Application/Json; charset=utf-8";
        Assert.Null(SupplyHttp.RequireContentType(parameterized, "application/json"));

        DefaultHttpContext metadata = Context();
        metadata.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.20");
        metadata.Request.Headers.UserAgent = new string('u', 600);
        Assert.Equal("192.0.2.20", SupplyHttp.RemoteIp(metadata));
        string summary = Assert.IsType<string>(SupplyHttp.UserAgent(metadata));
        Assert.StartsWith("sha256:", summary, StringComparison.Ordinal);
        Assert.Equal(71, summary.Length);
        Assert.DoesNotContain("uuu", summary, StringComparison.Ordinal);
        metadata.Request.Headers.UserAgent = " ";
        metadata.Connection.RemoteIpAddress = null;
        Assert.Null(SupplyHttp.UserAgent(metadata));
        Assert.Null(SupplyHttp.RemoteIp(metadata));
        Assert.Equal("\"v17\"", SupplyHttp.ETag(17));

        IResult invalidToken = SupplyHttp.InvalidUserToken(metadata);
        AssertStatus(invalidToken, StatusCodes.Status401Unauthorized);
        Assert.Equal("Bearer", metadata.Response.Headers.WWWAuthenticate);
    }

    [Fact]
    public void ErrorProjectionCoversEveryStableBranchAndFrozenPresentation()
    {
        (string Code, int Status, long? RetryAfter)[] cases =
        [
            ("role_required", 403, null),
            ("forbidden", 403, null),
            ("resource_not_found", 404, null),
            ("idempotency_conflict", 409, null),
            ("resource_conflict", 409, null),
            ("account_in_use", 409, null),
            ("channel_in_use", 409, null),
            ("version_conflict", 412, null),
            ("validation_failed", 422, null),
            ("group_account_binding_invalid", 422, null),
            ("invalid_request", 400, null),
            ("coordination_unavailable", 503, null),
            ("dependency_unavailable", 503, null),
            ("service_unavailable", 503, null),
            ("unexpected_internal", 500, null),
        ];

        foreach ((string code, int status, long? retryAfter) in cases)
        {
            DefaultHttpContext context = Context();
            ResultError error = new(
                code,
                "internal description",
                retryAfter,
                string.Equals(code, "version_conflict", StringComparison.Ordinal)
                    ? "\"v9\""
                    : null);

            IResult result = SupplyHttp.FromError(context, error);

            AssertStatus(result, status);
            if (string.Equals(code, "version_conflict", StringComparison.Ordinal))
            {
                Assert.Equal("\"v9\"", context.Response.Headers.ETag);
            }
            if (string.Equals(code, "coordination_unavailable", StringComparison.Ordinal)
                || string.Equals(code, "dependency_unavailable", StringComparison.Ordinal)
                || string.Equals(code, "service_unavailable", StringComparison.Ordinal))
            {
                Assert.Equal("1", context.Response.Headers.RetryAfter);
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            SupplyHttp.FieldError("/name", "is invalid");
        DefaultHttpContext frozen = Context();
        ResultErrorPresentation presentation = new(
            "resource_conflict",
            StatusCodes.Status418ImATeapot,
            "Frozen title",
            "Frozen detail",
            Retryable: true,
            RetryAfterSeconds: 7,
            Errors: errors);
        IValueHttpResult resultWithValue = Assert.IsAssignableFrom<IValueHttpResult>(
            SupplyHttp.FromError(
                frozen,
                new ResultError(
                    "resource_conflict",
                    "description",
                    RetryAfterSeconds: 7,
                    Presentation: presentation)));
        Assert.Equal(StatusCodes.Status418ImATeapot,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(resultWithValue).StatusCode);
        Assert.Equal("7", frozen.Response.Headers.RetryAfter);

        Assert.Throws<InvalidOperationException>(() => SupplyHttp.FromError(
            Context(),
            new ResultError(
                "resource_conflict",
                "description",
                Presentation: presentation with { Code = "different_code" })));
        Assert.Throws<InvalidOperationException>(() => SupplyHttp.FromError(
            Context(),
            new ResultError(
                "resource_conflict",
                "description",
                RetryAfterSeconds: 8,
                Presentation: presentation)));
    }

    private static DefaultHttpContext Context() => new()
    {
        TraceIdentifier = RequestId.ToString("D"),
    };

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(
        new ClaimsIdentity(claims, authenticationType: "unit-test"));

    private static void AssertStatus(IResult? result, int expected) =>
        Assert.Equal(
            expected,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(
                Assert.IsAssignableFrom<IResult>(result)).StatusCode);
}
#pragma warning restore MA0051
