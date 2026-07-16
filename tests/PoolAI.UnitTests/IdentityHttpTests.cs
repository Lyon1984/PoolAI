using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Endpoints;

namespace PoolAI.UnitTests;

public sealed class IdentityHttpTests
{
    private static readonly Guid RequestId = Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634041");
    private static readonly Guid UserId = Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634042");

    [Theory]
    [InlineData("admin", SystemRole.Admin)]
    [InlineData("operator", SystemRole.Operator)]
    [InlineData("auditor", SystemRole.Auditor)]
    [InlineData("user", SystemRole.User)]
    public void ActorClaimsAcceptEveryFrozenRole(string role, SystemRole expected)
    {
        DefaultHttpContext context = Context();
        context.User = Principal(
            new Claim("sub", UserId.ToString("D")),
            new Claim("role", role),
            new Claim("token_version", "7"));

        Assert.True(IdentityHttp.TryGetActor(context, out IdentityActor? actor));
        Assert.Equal(new EntityId(UserId), actor!.UserId);
        Assert.Equal(expected, actor.Role);
        Assert.Equal(7, actor.TokenVersion);
        Assert.Equal(actor, IdentityHttp.RequireActor(context));
    }

    [Fact]
    public void ActorClaimsSupportFrameworkFallbacksAndRejectMalformedPrincipals()
    {
        DefaultHttpContext fallback = Context();
        fallback.User = Principal(
            new Claim(ClaimTypes.NameIdentifier, UserId.ToString("D")),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("token_version", "1"));
        Assert.True(IdentityHttp.TryGetActor(fallback, out _));

        ClaimsPrincipal[] invalid =
        [
            Principal(new Claim("role", "admin"), new Claim("token_version", "1")),
            Principal(
                new Claim("sub", Guid.Empty.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "1")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "not-a-number")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "admin"),
                new Claim("token_version", "0")),
            Principal(
                new Claim("sub", UserId.ToString("D")),
                new Claim("role", "owner"),
                new Claim("token_version", "1")),
        ];

        foreach (ClaimsPrincipal principal in invalid)
        {
            DefaultHttpContext context = Context();
            context.User = principal;
            Assert.False(IdentityHttp.TryGetActor(context, out IdentityActor? actor));
            Assert.Null(actor);
            Assert.Throws<InvalidOperationException>(() => IdentityHttp.RequireActor(context));
        }
    }

    [Fact]
    public void RequestIdentifiersAndEntityIdentifiersFailClosed()
    {
        DefaultHttpContext valid = Context();
        Assert.Equal(new EntityId(RequestId), IdentityHttp.RequestId(valid));

        DefaultHttpContext invalid = Context();
        invalid.TraceIdentifier = "not-a-uuid";
        Assert.Throws<InvalidOperationException>(() => IdentityHttp.RequestId(invalid));

        Assert.False(IdentityHttp.TryGetEntityId(
            valid, Guid.Empty, "userId", out EntityId empty, out IResult? failure));
        Assert.Equal(default, empty);
        Assert.NotNull(failure);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);

        Assert.True(IdentityHttp.TryGetEntityId(
            valid, UserId, "userId", out EntityId entityId, out failure));
        Assert.Equal(new EntityId(UserId), entityId);
        Assert.Null(failure);
    }

    [Fact]
    public void ConditionalHeadersEnforceCanonicalVisibleAsciiForms()
    {
        DefaultHttpContext missing = Context();
        Assert.False(IdentityHttp.TryGetIdempotencyKey(missing, out _, out IResult? failure));
        Assert.Equal(428, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);

        foreach (string invalid in new[] { new string('x', 129), "invalid\tkey" })
        {
            DefaultHttpContext context = Context();
            context.Request.Headers["Idempotency-Key"] = invalid;
            Assert.False(IdentityHttp.TryGetIdempotencyKey(context, out _, out failure));
            Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);
        }

        DefaultHttpContext valid = Context();
        valid.Request.Headers["Idempotency-Key"] = "visible-key";
        Assert.True(IdentityHttp.TryGetIdempotencyKey(
            valid, out string? idempotencyKey, out failure));
        Assert.Equal("visible-key", idempotencyKey);
        Assert.Null(failure);

        DefaultHttpContext noIfMatch = Context();
        Assert.False(IdentityHttp.TryGetExpectedVersion(
            noIfMatch, out long expectedVersion, out failure));
        Assert.Equal(0, expectedVersion);
        Assert.Equal(428, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);

        foreach (string invalid in new[] { "v1", "W/\"v1\"", "\"v0\"", "\"vX\"" })
        {
            DefaultHttpContext context = Context();
            context.Request.Headers.IfMatch = invalid;
            Assert.False(IdentityHttp.TryGetExpectedVersion(
                context, out expectedVersion, out failure));
            Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(failure).StatusCode);
        }

        valid.Request.Headers.IfMatch = "\"v42\"";
        Assert.True(IdentityHttp.TryGetExpectedVersion(
            valid, out expectedVersion, out failure));
        Assert.Equal(42, expectedVersion);
        Assert.Null(failure);
    }

    [Fact]
    public void ContentTypesAllowParametersButRejectMissingOrDifferentMediaTypes()
    {
        DefaultHttpContext context = Context();
        Assert.NotNull(IdentityHttp.RequireContentType(context, "application/json"));

        context.Request.ContentType = "text/plain";
        Assert.NotNull(IdentityHttp.RequireContentType(context, "application/json"));

        context.Request.ContentType = "Application/Json; charset=utf-8";
        Assert.Null(IdentityHttp.RequireContentType(context, "application/json"));
    }

    [Fact]
    public void CreateAndUpdateValidationCoverAllFieldRules()
    {
        UserCreateRequest invalidCreate = new()
        {
            Email = "not-an-email",
            DisplayName = "invalid\u0001name",
            Role = (Role)999,
            TemporaryPassword = "short",
        };
        Assert.Equal(4, IdentityHttp.Validate(invalidCreate).Count);
        UserCreateRequest validCreate = new()
        {
            Email = "Person@BÜCHER.example",
            DisplayName = "Person",
            Role = Role.User,
            TemporaryPassword = "Temporary-Password-123!",
        };
        Assert.Empty(IdentityHttp.Validate(validCreate));

        Assert.Contains("/", IdentityHttp.Validate(new UserUpdateRequest()));
        Assert.Contains("/display_name", IdentityHttp.Validate(new UserUpdateRequest
        {
            DisplayName = " ",
        }));
        Assert.Contains("/role", IdentityHttp.Validate(new UserUpdateRequest
        {
            Role = (Role)999,
            Reason = "reason",
        }));
        Assert.Contains("/status", IdentityHttp.Validate(new UserUpdateRequest
        {
            Status = (UserStatus)999,
            Reason = "reason",
        }));
        Assert.Contains("/reason", IdentityHttp.Validate(new UserUpdateRequest
        {
            Role = Role.Admin,
        }));
        Assert.Contains("/reason", IdentityHttp.Validate(new UserUpdateRequest
        {
            Status = UserStatus.Disabled,
            Reason = "invalid\nreason",
        }));
        Assert.Empty(IdentityHttp.Validate(new UserUpdateRequest
        {
            DisplayName = "Renamed user",
        }));
    }

    [Fact]
    public void MailboxConversionAndContractMappingsCoverAllEnums()
    {
        Assert.False(IdentityHttp.TryNormalizeEmail(null, out _));
        Assert.False(IdentityHttp.TryNormalizeEmail("not-an-email", out _));
        Assert.True(IdentityHttp.TryNormalizeEmail(
            "Person@BÜCHER.example", out string normalized));
        Assert.Equal("Person@xn--bcher-kva.example", normalized);
        Assert.False(IdentityHttp.IsEmail("admin@invalid_domain.example"));

        Assert.Equal("\"v7\"", IdentityHttp.ETag(7));
        Assert.Equal(SystemRole.Admin, IdentityHttp.ToSystemRole(Role.Admin));
        Assert.Equal(SystemRole.Operator, IdentityHttp.ToSystemRole(Role.Operator));
        Assert.Equal(SystemRole.Auditor, IdentityHttp.ToSystemRole(Role.Auditor));
        Assert.Equal(SystemRole.User, IdentityHttp.ToSystemRole(Role.User));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityHttp.ToSystemRole((Role)999));
        Assert.Equal(UserLifecycle.Active, IdentityHttp.ToLifecycle(UserStatus.Active));
        Assert.Equal(UserLifecycle.Disabled, IdentityHttp.ToLifecycle(UserStatus.Disabled));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityHttp.ToLifecycle((UserStatus)999));

        DateTimeOffset timestamp = DateTimeOffset.Parse(
            "2026-07-17T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        foreach (SystemRole role in Enum.GetValues<SystemRole>())
        {
            User contract = IdentityHttp.ToContract(new UserView(
                new EntityId(UserId),
                "person@example.test",
                "Person",
                role,
                role == SystemRole.User ? UserLifecycle.Disabled : UserLifecycle.Active,
                7,
                timestamp,
                timestamp));
            Assert.Equal(UserId, contract.Id);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityHttp.ToContract(new UserView(
            new EntityId(UserId),
            "person@example.test",
            "Person",
            (SystemRole)999,
            UserLifecycle.Active,
            7,
            timestamp,
            timestamp)));
    }

    [Fact]
    public void ErrorMappingSetsFrozenHeadersAndSupportsEveryStableBranch()
    {
        (string Code, int Status, long? RetryAfter)[] cases =
        [
            ("role_required", 403, null),
            ("forbidden", 403, null),
            ("resource_not_found", 404, null),
            ("idempotency_conflict", 409, null),
            ("resource_conflict", 409, null),
            ("version_conflict", 412, null),
            ("password_reset_token_invalid", 401, null),
            ("password_policy_failed", 422, null),
            ("validation_failed", 422, null),
            ("invalid_request", 400, null),
            ("rate_limit_exceeded", 429, 9),
            ("coordination_unavailable", 503, null),
            ("dependency_unavailable", 503, null),
            ("service_unavailable", 503, null),
            ("unexpected_internal", 500, null),
        ];

        foreach ((string code, int status, long? retryAfter) in cases)
        {
            DefaultHttpContext context = Context();
            context.Request.Path = string.Equals(
                code,
                "password_policy_failed",
                StringComparison.Ordinal)
                ? "/api/v1/auth/reset-password"
                : "/api/v1/admin/users";
            ResultError error = new(
                code,
                "description",
                retryAfter,
                string.Equals(code, "version_conflict", StringComparison.Ordinal)
                    ? "\"v7\""
                    : null);

            IResult result = IdentityHttp.FromError(context, error);

            Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            if (string.Equals(code, "version_conflict", StringComparison.Ordinal))
            {
                Assert.Equal("\"v7\"", context.Response.Headers.ETag);
            }
            if (status == 401)
            {
                Assert.Equal("PasswordReset", context.Response.Headers.WWWAuthenticate);
            }
        }

    }

    [Fact]
    public void FrozenErrorPresentationMustMatchTheMappedError()
    {
        DefaultHttpContext frozenContext = Context();
        ResultErrorPresentation presentation = new(
            "resource_not_found",
            404,
            "Frozen title",
            "Frozen detail",
            Retryable: false);
        Assert.Equal(
            404,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(IdentityHttp.FromError(
                frozenContext,
                new ResultError(
                    "resource_not_found",
                    "description",
                    Presentation: presentation))).StatusCode);
        Assert.Throws<InvalidOperationException>(() => IdentityHttp.FromError(
            Context(),
            new ResultError(
                "resource_conflict",
                "description",
                Presentation: presentation)));
    }

    [Fact]
    public void RequestMetadataAndInvalidTokenHeadersAreBounded()
    {
        DefaultHttpContext context = Context();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Headers.UserAgent = new string('x', 600);
        Assert.Equal("192.0.2.10", IdentityHttp.RemoteIp(context));
        Assert.Equal(512, IdentityHttp.UserAgent(context)!.Length);

        context.Request.Headers.UserAgent = " ";
        Assert.Null(IdentityHttp.UserAgent(context));
        context.Connection.RemoteIpAddress = null;
        Assert.Null(IdentityHttp.RemoteIp(context));

        IResult result = IdentityHttp.InvalidUserToken(context);
        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate);
    }

    private static DefaultHttpContext Context()
    {
        DefaultHttpContext context = new();
        context.TraceIdentifier = RequestId.ToString("D");
        return context;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(
        new ClaimsIdentity(claims, authenticationType: "unit-test"));
}
