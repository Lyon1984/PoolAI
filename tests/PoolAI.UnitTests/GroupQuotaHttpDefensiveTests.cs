using System.Numerics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
    public void ActorAndRequestMetadataDefensesUseCanonicalClaimsAndDigests()
    {
        EntityId userId = EntityId.New();
        DefaultHttpContext context = Context();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString("D")),
            new Claim(ClaimTypes.Role, "operator"),
            new Claim("token_version", "2"),
        ],
        authenticationType: "unit-test"));

        Assert.True(GroupQuotaHttp.TryGetActor(context, out GroupActor? actor));
        Assert.Equal(userId, actor!.UserId);
        Assert.Equal(GroupControlRole.Operator, actor.Role);
        Assert.Equal(2, actor.TokenVersion);

        context.Request.Headers.UserAgent = "   ";
        Assert.Null(GroupQuotaHttp.UserAgent(context));

        context.Request.Headers.UserAgent = "defensive-agent";
        string expectedDigest = string.Concat(
            "sha256:",
            Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes("defensive-agent"))));
        Assert.Equal(expectedDigest, GroupQuotaHttp.UserAgent(context));

        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        Assert.Equal("192.0.2.10", GroupQuotaHttp.RemoteIp(context));

        context.TraceIdentifier = "not-a-request-id";
        Assert.Throws<InvalidOperationException>(() =>
            GroupQuotaHttp.RequestId(context));

        context.User = new ClaimsPrincipal();
        Assert.Throws<InvalidOperationException>(() =>
            GroupQuotaHttp.RequireActor(context));
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
        context.Features.GetRequiredFeature<IHttpRequestFeature>().Path = null!;

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

    [Theory]
    [InlineData("""{"name":"Research","total_tokens":"1"}""")]
    [InlineData("""{"name":"Research","total_tokens":1.5}""")]
    [InlineData("""{"name":"Research","total_tokens":1e1}""")]
    [InlineData("""{"name":"Research","total_tokens":0}""")]
    [InlineData("""{"name":"Research","total_tokens":-1}""")]
    [InlineData("""{"name":"Research","total_tokens":9007199254740992}""")]
    public void GroupCreateParserRejectsNonCanonicalOrUnsafeTokenInput(
        string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        bool accepted = GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.False(accepted);
        Assert.Null(parsed);
        Assert.Contains("/total_tokens", errors);
    }

    [Fact]
    public void GroupCreateParserRejectsAdditionalProperties()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"name":"Research","total_tokens":1,"unexpected":true}""");

        bool accepted = GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.False(accepted);
        Assert.Null(parsed);
        Assert.Contains("/unexpected", errors);
    }

    [Fact]
    public void ParsersRejectMissingRequiredTokenProperties()
    {
        using JsonDocument createDocument = JsonDocument.Parse(
            """{"name":"Research"}""");
        Assert.False(GroupQuotaHttp.TryParseGroupCreateRequest(
            createDocument.RootElement,
            out _,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> createErrors));
        Assert.Contains("/total_tokens", createErrors);

        using JsonDocument mutationDocument = JsonDocument.Parse(
            """{"reason":"capacity review"}""");
        Assert.False(GroupQuotaHttp.TryParseQuotaMutationRequest(
            mutationDocument.RootElement,
            "new_total_tokens",
            out _,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> mutationErrors));
        Assert.Contains("/new_total_tokens", mutationErrors);
    }

    [Fact]
    public void ContentTypeParserAcceptsAnExactMediaTypeWithoutParameters()
    {
        DefaultHttpContext context = Context();
        context.Request.ContentType = "application/json";

        Assert.Null(GroupQuotaHttp.RequireContentType(
            context,
            "application/json"));

        context.Request.ContentType = null;
        Assert.NotNull(GroupQuotaHttp.RequireContentType(
            context,
            "application/json"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("1")]
    public void GroupAndQuotaParsersRejectNonObjectBodies(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.False(GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? create,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> createErrors));
        Assert.Null(create);
        Assert.Contains("/", createErrors);

        Assert.False(GroupQuotaHttp.TryParseQuotaMutationRequest(
            document.RootElement,
            "new_total_tokens",
            out GroupQuotaHttp.ParsedQuotaMutationRequest? mutation,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> mutationErrors));
        Assert.Null(mutation);
        Assert.Contains("/", mutationErrors);
    }

    [Theory]
    [InlineData("""{"name":42,"total_tokens":1}""", "/name")]
    [InlineData("""{"name":"Research","description":true,"total_tokens":1}""", "/description")]
    [InlineData("""{"name":"Research","description":"\uD800","total_tokens":1}""", "/description")]
    public void GroupCreateParserRejectsInvalidTextFieldShapes(
        string json,
        string expectedPointer)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.False(GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors));
        Assert.Null(parsed);
        Assert.Contains(expectedPointer, errors);
    }

    [Theory]
    [InlineData("""{"name":"\uD800","total_tokens":1}""", "/name")]
    [InlineData("""{"name":"Research","total_tokens":1,"\uD800":true}""", "/")]
    public void GroupCreateParserRejectsInvalidUnicodeWithoutThrowing(
        string json,
        string expectedPointer)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        bool accepted = GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.False(accepted);
        Assert.Null(parsed);
        Assert.Contains(expectedPointer, errors);
    }

    [Fact]
    public void GroupCreateParserAcceptsTheExactSafeIntegerBoundary()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"name":" Research ","description":null,"total_tokens":9007199254740991}""");

        bool accepted = GroupQuotaHttp.TryParseGroupCreateRequest(
            document.RootElement,
            out GroupQuotaHttp.ParsedGroupCreateRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.True(accepted);
        Assert.Empty(errors);
        Assert.Equal(" Research ", parsed!.Name);
        Assert.Null(parsed.Description);
        Assert.Equal(9_007_199_254_740_991, parsed.TotalTokens);
    }

    [Theory]
    [InlineData("new_total_tokens", """{"new_total_tokens":"1","reason":"adjust"}""", "/new_total_tokens")]
    [InlineData("new_total_tokens", """{"new_total_tokens":1.0,"reason":"adjust"}""", "/new_total_tokens")]
    [InlineData("new_total_tokens", """{"new_total_tokens":1e1,"reason":"adjust"}""", "/new_total_tokens")]
    [InlineData("new_total_tokens", """{"new_total_tokens":1,"reason":" "}""", "/reason")]
    [InlineData("new_total_tokens", """{"new_total_tokens":1,"reason":"\uD800"}""", "/reason")]
    [InlineData("new_total_tokens", """{"new_total_tokens":1,"reason":"valid","\uD800":true}""", "/")]
    [InlineData("total_tokens", """{"total_tokens":1,"reason":"reset","unexpected":true}""", "/unexpected")]
    public void QuotaMutationParserReturnsFieldLevelValidation(
        string tokenProperty,
        string json,
        string expectedPointer)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        bool accepted = GroupQuotaHttp.TryParseQuotaMutationRequest(
            document.RootElement,
            tokenProperty,
            out GroupQuotaHttp.ParsedQuotaMutationRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.False(accepted);
        Assert.Null(parsed);
        Assert.Contains(expectedPointer, errors);
    }

    [Fact]
    public void QuotaMutationParserAndContractPreserveBigIntegerValues()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"new_total_tokens":9007199254740991,"reason":"capacity review"}""");
        Assert.True(GroupQuotaHttp.TryParseQuotaMutationRequest(
            document.RootElement,
            "new_total_tokens",
            out GroupQuotaHttp.ParsedQuotaMutationRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors));
        Assert.Empty(errors);
        Assert.Equal(9_007_199_254_740_991, parsed!.TotalTokens);
        Assert.Equal("capacity review", parsed.Reason);

        EntityId groupId = EntityId.New();
        EntityId periodId = EntityId.New();
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-07-31T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        GroupQuotaView view = new(
            groupId,
            periodId,
            GroupPoolQuotaStatus.Exhausted,
            BigInteger.One,
            BigInteger.Parse("900719925474099100000", System.Globalization.CultureInfo.InvariantCulture),
            new BigInteger(20),
            BigInteger.Zero,
            BigInteger.Parse("900719925474099099999", System.Globalization.CultureInfo.InvariantCulture),
            now,
            null,
            9,
            now);

        PoolAI.Contracts.Generated.GroupQuota contract = GroupQuotaHttp.ToContract(view);

        Assert.Equal("exhausted", contract.Status);
        Assert.Equal("900719925474099100000", contract.ConsumedTokens);
        Assert.Equal("900719925474099099999", contract.OverageTokens);
        Assert.True(contract.PeriodEndedAt.HasValue);
        Assert.Null(contract.PeriodEndedAt.Value);
    }

    [Fact]
    public void QuotaMutationParserRejectsNonStringReason()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"new_total_tokens":1,"reason":42}""");

        Assert.False(GroupQuotaHttp.TryParseQuotaMutationRequest(
            document.RootElement,
            "new_total_tokens",
            out GroupQuotaHttp.ParsedQuotaMutationRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors));
        Assert.Null(parsed);
        Assert.Contains("/reason", errors);
    }

    [Fact]
    public void QuotaContractCoversEveryStatusAndRejectsUnknownValues()
    {
        Assert.Equal(
            "active",
            GroupQuotaHttp.ToContract(
                QuotaView(GroupPoolQuotaStatus.Active)).Status);
        Assert.Equal(
            "disabled",
            GroupQuotaHttp.ToContract(
                QuotaView(GroupPoolQuotaStatus.Disabled)).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupQuotaHttp.ToContract(
                QuotaView((GroupPoolQuotaStatus)999)));
    }

    [Fact]
    public void QuotaMutationReasonUsesUnicodeScalarLengthWithoutRewriting()
    {
        string reason = string.Concat(
            "  audit\n",
            string.Concat(Enumerable.Repeat("😀", 300)),
            "  ");
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                new_total_tokens = 1,
                reason,
            }));

        bool accepted = GroupQuotaHttp.TryParseQuotaMutationRequest(
            document.RootElement,
            "new_total_tokens",
            out GroupQuotaHttp.ParsedQuotaMutationRequest? parsed,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> errors);

        Assert.True(accepted);
        Assert.Empty(errors);
        Assert.Equal(reason, parsed!.Reason);

        string tooLongReason = string.Concat(Enumerable.Repeat("😀", 501));
        using JsonDocument tooLongDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(new
            {
                new_total_tokens = 1,
                reason = tooLongReason,
            }));
        Assert.False(GroupQuotaHttp.TryParseQuotaMutationRequest(
            tooLongDocument.RootElement,
            "new_total_tokens",
            out _,
            out IReadOnlyDictionary<string, IReadOnlyList<string>> tooLongErrors));
        Assert.Contains("/reason", tooLongErrors);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void QuotaReasonContractRejectsIsolatedUtf16Surrogates(
        int invalidCodeUnit)
    {
        string invalidReason = new((char)invalidCodeUnit, 1);

        Assert.False(
            GroupQuotaHttp.SatisfiesQuotaReasonContract(invalidReason));
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

    private static GroupQuotaView QuotaView(GroupPoolQuotaStatus status)
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2026-07-31T00:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        return new GroupQuotaView(
            EntityId.New(),
            EntityId.New(),
            status,
            BigInteger.One,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.One,
            BigInteger.Zero,
            now,
            null,
            1,
            now);
    }
}
