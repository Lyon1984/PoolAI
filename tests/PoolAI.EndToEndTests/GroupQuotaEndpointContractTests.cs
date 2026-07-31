using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.EndToEndTests;

public sealed class GroupQuotaEndpointContractTests
{
    private const string QuotaTotal = "9007199254740991";
    private const string QuotaConsumed = "9007199254740992";
    private static readonly string[] MultipleIdempotencyKeys =
        ["first-key", "second-key"];
    private const string QuotaReserved =
        "123456789012345678901234567890";
    private static readonly EntityId ActorId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634080"));
    private static readonly EntityId GroupId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634081"));
    private static readonly EntityId QuotaPeriodId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634082"));
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-07-17T08:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task AdminListSerializesEveryGroupLifecycleAndPaginationShape()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.ListResult = Result.Success(new GroupPage(
            [
                View(GroupLifecycle.Active, version: 7, name: "Active Group"),
                View(GroupLifecycle.Disabled, version: 8, name: "Disabled Group"),
                View(GroupLifecycle.Archived, version: 9, name: "Archived Group"),
            ],
            "next/page",
            HasMore: true));
        using HttpClient operatorClient = AuthenticatedClient(factory, "operator");

        using HttpResponseMessage first = await operatorClient.GetAsync(
            "/api/v1/admin/groups?cursor=previous%2Fpage&limit=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/json", first.Content.Headers.ContentType?.MediaType);
        AssertRequestId(first);
        await AssertLifecyclePageAsync(first).ConfigureAwait(true);

        ListGroupsQuery firstQuery = Assert.IsType<ListGroupsQuery>(
            factory.UseCases.LastListQuery);
        Assert.Equal(GroupControlRole.Operator, firstQuery.Actor.Role);
        Assert.Equal(ActorId, firstQuery.Actor.UserId);
        Assert.Equal(7, firstQuery.Actor.TokenVersion);
        Assert.Equal("previous/page", firstQuery.Cursor);
        Assert.Equal(100, firstQuery.Limit);

        factory.UseCases.ListResult = Result.Success(new GroupPage([], null, HasMore: false));
        using HttpClient auditorClient = AuthenticatedClient(factory, "auditor");
        using HttpResponseMessage second = await auditorClient.GetAsync(
            "/api/v1/admin/groups",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(second).ConfigureAwait(true))
        {
            Assert.Empty(document.RootElement.GetProperty("data").EnumerateArray());
            JsonElement page = document.RootElement.GetProperty("page");
            Assert.False(page.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, page.GetProperty("next_cursor").ValueKind);
        }

        ListGroupsQuery secondQuery = Assert.IsType<ListGroupsQuery>(
            factory.UseCases.LastListQuery);
        Assert.Equal(GroupControlRole.Auditor, secondQuery.Actor.Role);
        Assert.Null(secondQuery.Cursor);
        Assert.Equal(50, secondQuery.Limit);

    }

    [Fact]
    public async Task AdminGetReturnsTheGroupAndStrongEntityTag()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.GetResult = Result.Success(
            View(GroupLifecycle.Active, version: 12, name: "Detailed Group"));
        using HttpClient client = AuthenticatedClient(factory, "auditor");

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v12\"", response.Headers.ETag?.Tag);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true);
        Assert.Equal(GroupId.Value, document.RootElement.GetProperty("id").GetGuid());
        Assert.Equal("Detailed Group", document.RootElement.GetProperty("name").GetString());
        Assert.Equal("active", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("description", document.RootElement.GetProperty("description").GetString());
        GetGroupQuery query = Assert.IsType<GetGroupQuery>(factory.UseCases.LastGetQuery);
        Assert.Equal(GroupId, query.GroupId);
        Assert.Equal(GroupControlRole.Auditor, query.Actor.Role);
    }

    [Fact]
    public async Task AdminCreateReturnsFrozenHeadersAndPassesAuditableTransportMetadata()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.CreateResult = Result.Success(new GroupCommandOutcome(
            StatusCodes.Status201Created,
            IsReplay: false,
            View(GroupLifecycle.Disabled, version: 3, name: "Research", description: null),
            "\"v3\""));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        string userAgent = new('a', 600);
        using HttpRequestMessage request = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new
            {
                name = "Research",
                description = (string?)null,
                total_tokens = 9_007_199_254_740_991,
            },
            idempotencyKey: "group-create-success");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v3\"", response.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            response.Headers.Location?.OriginalString);
        string responseRequestId = AssertRequestId(response);
        using (JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true))
        {
            Assert.Equal("Research", document.RootElement.GetProperty("name").GetString());
            Assert.Equal("disabled", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("description").ValueKind);
        }

        CreateGroupCommand command = Assert.IsType<CreateGroupCommand>(
            factory.UseCases.LastCreateCommand);
        Assert.Equal(Guid.Parse(responseRequestId), command.RequestId.Value);
        Assert.Equal(ActorId, command.Actor.UserId);
        Assert.Equal(GroupControlRole.Admin, command.Actor.Role);
        Assert.Equal("group-create-success", command.IdempotencyKey);
        Assert.Null(command.Description);
        Assert.Equal(9_007_199_254_740_991, command.TotalTokens);
        Assert.Equal(UserAgentDigest(userAgent), command.UserAgent);

    }

    [Fact]
    public async Task AdminCreateReplayUsesTheStoredLocationAndMissingOptionalMetadata()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.CreateResult = Result.Success(new GroupCommandOutcome(
            StatusCodes.Status201Created,
            IsReplay: true,
            View(GroupLifecycle.Disabled, version: 4, name: "Replay"),
            "\"v4\"",
            "/api/v1/admin/groups/replayed-location"));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage replay = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new { name = "Replay", total_tokens = 1 },
            idempotencyKey: "group-create-replay");

        using HttpResponseMessage replayResponse = await client.SendAsync(
            replay,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(
            "/api/v1/admin/groups/replayed-location",
            replayResponse.Headers.Location?.OriginalString);
        CreateGroupCommand replayCommand = Assert.IsType<CreateGroupCommand>(
            factory.UseCases.LastCreateCommand);
        Assert.Null(replayCommand.Description);
        Assert.Null(replayCommand.UserAgent);
    }

    [Fact]
#pragma warning disable MA0051 // The mathematical-integer positive/negative matrix is cohesive.
    public async Task AdminCreateTotalTokensRequiresAMathematicallyIntegralSafeJsonNumber()
    {
        // Governing contract: AC-026 and the OpenAPI 3.1 JSON Schema accept a
        // positive mathematical integer in the JavaScript-safe range. JSON
        // strings, non-integral values, and out-of-range values fail before
        // the Group create use case.
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        string[] invalidTotalTokens =
        [
            "\"1\"",
            "1.5",
            "1e-1",
            "100e-3",
            "1.0000000000000001",
            "0",
            "-1",
            "9007199254740992",
            "9.007199254740992e15",
        ];

        for (int index = 0; index < invalidTotalTokens.Length; index++)
        {
            using HttpRequestMessage request = RawJsonCommand(
                $$"""{"name":"Valid","total_tokens":{{invalidTotalTokens[index]}}}""",
                $"group-create-invalid-total-{index}");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(
                response,
                "validation_failed",
                "/total_tokens").ConfigureAwait(true);
        }

        Assert.Equal(0, factory.UseCases.CreateCalls);

        (string Number, long Expected)[] validTotalTokens =
        [
            ("1.0", 1),
            ("1e3", 1_000),
            ("10e-1", 1),
            ("0.001e3", 1),
            ("9.007199254740991e15", 9_007_199_254_740_991),
        ];
        for (int index = 0; index < validTotalTokens.Length; index++)
        {
            (string number, long expected) = validTotalTokens[index];
            using HttpRequestMessage valid = RawJsonCommand(
                $$"""{"name":"Valid","total_tokens":{{number}}}""",
                $"group-create-safe-integer-{index}");
            using HttpResponseMessage validResponse = await client.SendAsync(
                valid,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Created, validResponse.StatusCode);
            Assert.Equal(
                expected,
                Assert.IsType<CreateGroupCommand>(
                    factory.UseCases.LastCreateCommand).TotalTokens);
        }

        Assert.Equal(validTotalTokens.Length, factory.UseCases.CreateCalls);
    }
#pragma warning restore MA0051

    [Fact]
    public async Task AdminCreateRejectsAdditionalProperties()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = RawJsonCommand(
            """{"name":"Valid","total_tokens":1,"unexpected":true}""",
            "group-create-additional-property");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(
            response,
            "validation_failed",
            "/unexpected").ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.CreateCalls);
    }

    [Theory]
    [InlineData("""{"name":"\uD800","total_tokens":1}""", "/name")]
    [InlineData("""{"name":"Valid","total_tokens":1,"\uD800":true}""", "/")]
    public async Task AdminCreateRejectsInvalidUnicodeWithoutServerError(
        string body,
        string expectedPointer)
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = RawJsonCommand(
            body,
            $"group-create-invalid-unicode-{expectedPointer.Length}");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(
            response,
            "validation_failed",
            expectedPointer).ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.CreateCalls);
    }

    [Theory]
    [InlineData("operator", GroupControlRole.Operator)]
    [InlineData("auditor", GroupControlRole.Auditor)]
    public async Task OperatorAndAuditorReadCanonicalBigIntegerQuotaStrings(
        string role,
        GroupControlRole expectedRole)
    {
        await using GroupApiFactory factory = new();
        GroupQuotaView quota = LargeQuotaView(
            GroupPoolQuotaStatus.Exhausted,
            version: 31);
        factory.UseCases.GetQuotaResult = Result.Success(quota);
        using HttpClient client = AuthenticatedClient(factory, role);

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v31\"", response.Headers.ETag?.Tag);
        AssertRequestId(response);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true);
        JsonElement body = document.RootElement;
        Assert.Equal(GroupId.Value, body.GetProperty("group_id").GetGuid());
        Assert.Equal(QuotaPeriodId.Value, body.GetProperty("period_id").GetGuid());
        Assert.Equal("exhausted", body.GetProperty("status").GetString());
        Assert.Equal(QuotaTotal, body.GetProperty("total_tokens").GetString());
        Assert.Equal(QuotaConsumed, body.GetProperty("consumed_tokens").GetString());
        Assert.Equal(QuotaReserved, body.GetProperty("reserved_tokens").GetString());
        Assert.Equal("0", body.GetProperty("remaining_tokens").GetString());
        Assert.Equal("1", body.GetProperty("overage_tokens").GetString());
        Assert.Equal(
            Timestamp.AddDays(-1),
            body.GetProperty("period_started_at").GetDateTimeOffset());
        Assert.Equal(
            JsonValueKind.Null,
            body.GetProperty("period_ended_at").ValueKind);
        Assert.Equal(Timestamp, body.GetProperty("updated_at").GetDateTimeOffset());
        Assert.Equal(31, body.GetProperty("version").GetInt64());

        GetGroupQuotaQuery query = Assert.IsType<GetGroupQuotaQuery>(
            factory.UseCases.LastGetQuotaQuery);
        Assert.Equal(GroupId, query.GroupId);
        Assert.Equal(expectedRole, query.Actor.Role);
        Assert.Equal(ActorId, query.Actor.UserId);
        Assert.Equal(1, factory.UseCases.GetQuotaCalls);
    }

    [Fact]
    public async Task UserQuotaReadIsForbiddenBeforeTheUseCase()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "user");

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, "role_required").ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.GetQuotaCalls);
    }

    [Fact]
    public async Task QuotaReadRejectsEmptyIdentifiersAndMapsApplicationFailures()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        using HttpResponseMessage emptyId = await client.GetAsync(
            $"/api/v1/admin/groups/{Guid.Empty:D}/quota",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);
        await AssertProblemAsync(emptyId, "invalid_request", "/groupId")
            .ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.GetQuotaCalls);

        factory.UseCases.GetQuotaResult = Result.Failure<GroupQuotaView>(
            GroupErrorCodes.ResourceNotFound,
            "synthetic missing quota");
        using HttpResponseMessage missing = await client.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertProblemAsync(missing, "resource_not_found")
            .ConfigureAwait(true);
        Assert.Equal(1, factory.UseCases.GetQuotaCalls);
    }

    [Fact]
    public async Task AdminAdjustMapsHeadersTransportAndQuotaCommand()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.AdjustQuotaResult = Result.Success(
            new GroupQuotaCommandOutcome(
                StatusCodes.Status200OK,
                IsReplay: false,
                LargeQuotaView(GroupPoolQuotaStatus.Exhausted, version: 42),
                "\"v42\""));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        string userAgent = new('q', 600);
        using HttpRequestMessage adjust = RawJsonCommand(
            """{"new_total_tokens":9007199254740991,"reason":"capacity review"}""",
            "quota-adjust-success",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/adjust",
            ifMatch: "\"v41\"");
        adjust.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using HttpResponseMessage adjustResponse = await client.SendAsync(
            adjust,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, adjustResponse.StatusCode);
        Assert.Equal("\"v42\"", adjustResponse.Headers.ETag?.Tag);
        string adjustRequestId = AssertRequestId(adjustResponse);
        AdjustGroupQuotaCommand adjustCommand =
            Assert.IsType<AdjustGroupQuotaCommand>(
                factory.UseCases.LastAdjustQuotaCommand);
        Assert.Equal(Guid.Parse(adjustRequestId), adjustCommand.RequestId.Value);
        Assert.Equal(ActorId, adjustCommand.Actor.UserId);
        Assert.Equal(GroupControlRole.Admin, adjustCommand.Actor.Role);
        Assert.Equal(GroupId, adjustCommand.GroupId);
        Assert.Equal(41, adjustCommand.ExpectedVersion);
        Assert.Equal(9_007_199_254_740_991, adjustCommand.NewTotalTokens);
        Assert.Equal("capacity review", adjustCommand.Reason);
        Assert.Equal("quota-adjust-success", adjustCommand.IdempotencyKey);
        Assert.Null(adjustCommand.IpAddress);
        Assert.Equal(UserAgentDigest(userAgent), adjustCommand.UserAgent);
        Assert.Equal(1, factory.UseCases.AdjustQuotaCalls);
    }

    [Fact]
    public async Task AdminResetMapsHeadersTransportAndQuotaCommand()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.ResetQuotaResult = Result.Success(
            new GroupQuotaCommandOutcome(
                StatusCodes.Status200OK,
                IsReplay: false,
                ActiveQuotaView(totalTokens: 7000, version: 43),
                "\"v43\""));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        string userAgent = new('q', 600);
        using HttpRequestMessage reset = RawJsonCommand(
            """{"total_tokens":7000,"reason":"manual period reset"}""",
            "quota-reset-success",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/reset",
            ifMatch: "\"v42\"");
        reset.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        using HttpResponseMessage resetResponse = await client.SendAsync(
            reset,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resetResponse.StatusCode);
        Assert.Equal("\"v43\"", resetResponse.Headers.ETag?.Tag);
        string resetRequestId = AssertRequestId(resetResponse);
        ResetGroupQuotaCommand resetCommand = Assert.IsType<ResetGroupQuotaCommand>(
            factory.UseCases.LastResetQuotaCommand);
        Assert.Equal(Guid.Parse(resetRequestId), resetCommand.RequestId.Value);
        Assert.Equal(ActorId, resetCommand.Actor.UserId);
        Assert.Equal(GroupControlRole.Admin, resetCommand.Actor.Role);
        Assert.Equal(GroupId, resetCommand.GroupId);
        Assert.Equal(42, resetCommand.ExpectedVersion);
        Assert.Equal(7000, resetCommand.TotalTokens);
        Assert.Equal("manual period reset", resetCommand.Reason);
        Assert.Equal("quota-reset-success", resetCommand.IdempotencyKey);
        Assert.Null(resetCommand.IpAddress);
        Assert.Equal(UserAgentDigest(userAgent), resetCommand.UserAgent);
        Assert.Equal(1, factory.UseCases.ResetQuotaCalls);
    }

    [Fact]
    public async Task QuotaMutationDefensiveOutcomesFailClosedAndMapErrors()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        factory.UseCases.ReturnInvalidQuotaAuthorizationSuccess = true;
        using HttpRequestMessage invalidAuthorization = RawJsonCommand(
            """{"new_total_tokens":2,"reason":"authorization defense"}""",
            "quota-adjust-invalid-authorization",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/adjust",
            ifMatch: "\"v1\"");

        using HttpResponseMessage invalidAuthorizationResponse =
            await client.SendAsync(
                invalidAuthorization,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            invalidAuthorizationResponse.StatusCode);
        await AssertProblemAsync(
            invalidAuthorizationResponse,
            "internal_error").ConfigureAwait(true);
        Assert.Equal(1, factory.UseCases.QuotaAuthorizationCalls);
        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);

        factory.UseCases.ReturnInvalidQuotaAuthorizationSuccess = false;
        factory.UseCases.AdjustQuotaResult =
            Result.Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.VersionConflict,
                "synthetic stale quota version",
                etag: "\"v9\"");
        using HttpRequestMessage stale = RawJsonCommand(
            """{"new_total_tokens":2,"reason":"stale total"}""",
            "quota-adjust-stale",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/adjust",
            ifMatch: "\"v1\"");

        using HttpResponseMessage staleResponse = await client.SendAsync(
            stale,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        Assert.Equal("\"v9\"", staleResponse.Headers.ETag?.Tag);
        await AssertProblemAsync(
            staleResponse,
            "version_conflict",
            expectedRetryable: true).ConfigureAwait(true);
        Assert.Equal(2, factory.UseCases.QuotaAuthorizationCalls);
        Assert.Equal(1, factory.UseCases.AdjustQuotaCalls);
    }

    [Fact]
    public async Task QuotaMutationFieldsFailBeforeEitherUseCase()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        await AssertInvalidQuotaMutationFieldsAsync(
            client,
            "adjust",
            "new_total_tokens").ConfigureAwait(true);
        await AssertInvalidQuotaMutationFieldsAsync(
            client,
            "reset",
            "total_tokens").ConfigureAwait(true);

        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
        Assert.Equal(0, factory.UseCases.ResetQuotaCalls);
    }

    [Theory]
    [InlineData("adjust", "new_total_tokens", "1.0", 1L)]
    [InlineData("adjust", "new_total_tokens", "1e3", 1_000L)]
    [InlineData("reset", "total_tokens", "10e-1", 1L)]
    [InlineData(
        "reset",
        "total_tokens",
        "9.007199254740991e15",
        9_007_199_254_740_991L)]
    public async Task QuotaMutationAcceptsMathematicallyIntegralJsonForms(
        string operation,
        string tokenProperty,
        string number,
        long expected)
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = RawJsonCommand(
            QuotaMutationBody(tokenProperty, number),
            $"quota-{operation}-mathematical-integer",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/{operation}",
            ifMatch: "\"v1\"");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (string.Equals(operation, "adjust", StringComparison.Ordinal))
        {
            Assert.Equal(
                expected,
                Assert.IsType<AdjustGroupQuotaCommand>(
                    factory.UseCases.LastAdjustQuotaCommand).NewTotalTokens);
            Assert.Equal(1, factory.UseCases.AdjustQuotaCalls);
            Assert.Equal(0, factory.UseCases.ResetQuotaCalls);
        }
        else
        {
            Assert.Equal(
                expected,
                Assert.IsType<ResetGroupQuotaCommand>(
                    factory.UseCases.LastResetQuotaCommand).TotalTokens);
            Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
            Assert.Equal(1, factory.UseCases.ResetQuotaCalls);
        }
    }

    [Fact]
    public async Task QuotaMutationTransportAndPathFailuresDoNotReachUseCases()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        await AssertQuotaMutationTransportFailuresAsync(
            client,
            "adjust",
            "new_total_tokens").ConfigureAwait(true);
        await AssertQuotaMutationTransportFailuresAsync(
            client,
            "reset",
            "total_tokens").ConfigureAwait(true);

        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
        Assert.Equal(0, factory.UseCases.ResetQuotaCalls);
    }

    [Theory]
    [InlineData("adjust")]
    [InlineData("reset")]
    public async Task AdminMalformedQuotaJsonFailsAfterAuthorizationPreflight(
        string operation)
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = RawJsonCommand(
            "{",
            $"quota-{operation}-malformed-json",
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/{operation}",
            ifMatch: "\"v1\"");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_request", "/").ConfigureAwait(true);
        Assert.Equal(1, factory.UseCases.QuotaAuthorizationCalls);
        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
        Assert.Equal(0, factory.UseCases.ResetQuotaCalls);
    }

    [Theory]
    [InlineData("operator", GroupControlRole.Operator)]
    [InlineData("auditor", GroupControlRole.Auditor)]
    [InlineData("user", GroupControlRole.User)]
    public async Task NonAdminQuotaMutationsReachApplicationPolicyForAuditing(
        string role,
        GroupControlRole expectedRole)
    {
        // Governing contract: AC-004 requires every authenticated non-Admin
        // attempt to reach application Policy before transport/body/header
        // validation, so even this deliberately malformed request is 403 and
        // can be audited without reading its body.
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, role);
        using HttpRequestMessage adjust = new(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/adjust")
        {
            Content = new StringContent("{", Encoding.UTF8, "text/plain"),
        };
        using HttpResponseMessage adjustResponse = await client.SendAsync(
            adjust,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, adjustResponse.StatusCode);
        await AssertProblemAsync(adjustResponse, "role_required").ConfigureAwait(true);
        Assert.Equal(
            expectedRole,
            factory.UseCases.LastQuotaAuthorizationCommand!.Actor.Role);
        Assert.Equal(
            QuotaMutationOperation.AdjustTotal,
            factory.UseCases.LastQuotaAuthorizationCommand.Operation);
        Assert.Equal(
            QuotaMutationIdempotencyKeyStatus.Missing,
            factory.UseCases.LastQuotaAuthorizationCommand.IdempotencyKeyAudit.Status);

        using HttpRequestMessage reset = new(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/reset")
        {
            Content = new StringContent("{", Encoding.UTF8, "text/plain"),
        };
        using HttpResponseMessage resetResponse = await client.SendAsync(
            reset,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, resetResponse.StatusCode);
        await AssertProblemAsync(resetResponse, "role_required").ConfigureAwait(true);
        Assert.Equal(
            expectedRole,
            factory.UseCases.LastQuotaAuthorizationCommand!.Actor.Role);
        Assert.Equal(
            QuotaMutationOperation.ResetPeriod,
            factory.UseCases.LastQuotaAuthorizationCommand.Operation);
        Assert.Equal(
            QuotaMutationIdempotencyKeyStatus.Missing,
            factory.UseCases.LastQuotaAuthorizationCommand.IdempotencyKeyAudit.Status);
        Assert.Equal(2, factory.UseCases.QuotaAuthorizationCalls);
        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
        Assert.Equal(0, factory.UseCases.ResetQuotaCalls);
    }

    [Fact]
    public async Task QuotaAuthorizationPreflightClassifiesIdempotencyHeadersWithoutReadingBody()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "operator");
        (string? Single, string[]? Multiple, QuotaMutationIdempotencyKeyStatus Status,
            string? ValidValue)[] cases =
        [
            (null, null, QuotaMutationIdempotencyKeyStatus.Missing, null),
            (string.Empty, null, QuotaMutationIdempotencyKeyStatus.Missing, null),
            ("bad key", null, QuotaMutationIdempotencyKeyStatus.Invalid, null),
            (null, ["first-key", "second-key"],
                QuotaMutationIdempotencyKeyStatus.Multiple, null),
            ("valid-audit-key", null, QuotaMutationIdempotencyKeyStatus.Valid,
                "valid-audit-key"),
        ];

        foreach ((string? single, string[]? multiple,
                     QuotaMutationIdempotencyKeyStatus status, string? validValue) in cases)
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"/api/v1/admin/groups/{GroupId.Value:D}/quota/adjust")
            {
                Content = new StringContent("{", Encoding.UTF8, "text/plain"),
            };
            if (single is not null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", single);
            }
            else if (multiple is not null)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", multiple);
            }

            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            await AssertProblemAsync(response, "role_required").ConfigureAwait(true);
            QuotaMutationIdempotencyKeyAuditInput input =
                factory.UseCases.LastQuotaAuthorizationCommand!.IdempotencyKeyAudit;
            Assert.Equal(status, input.Status);
            Assert.Equal(validValue, input.ValidValue);
        }

        Assert.Equal(cases.Length, factory.UseCases.QuotaAuthorizationCalls);
        Assert.Equal(0, factory.UseCases.AdjustQuotaCalls);
    }

    [Fact]
    public async Task AdminUpdateMapsDisabledMetadataPatchToTheGroupCommand()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        factory.UseCases.UpdateResult = Result.Success(new GroupCommandOutcome(
            StatusCodes.Status200OK,
            IsReplay: false,
            View(GroupLifecycle.Disabled, version: 8, name: "Renamed"),
            "\"v8\""));
        using HttpRequestMessage disabled = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new
            {
                name = "Renamed",
                description = (string?)null,
                status = "disabled",
                reason = "maintenance",
            },
            "application/merge-patch+json",
            "group-disable",
            "\"v7\"");

        using HttpResponseMessage disabledResponse = await client.SendAsync(
            disabled,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
        Assert.Equal("\"v8\"", disabledResponse.Headers.ETag?.Tag);
        UpdateGroupCommand disabledCommand = Assert.IsType<UpdateGroupCommand>(
            factory.UseCases.LastUpdateCommand);
        Assert.Equal(GroupId, disabledCommand.GroupId);
        Assert.Equal(7, disabledCommand.ExpectedVersion);
        Assert.True(disabledCommand.HasName);
        Assert.Equal("Renamed", disabledCommand.Name);
        Assert.True(disabledCommand.HasDescription);
        Assert.Null(disabledCommand.Description);
        Assert.True(disabledCommand.HasStatus);
        Assert.Equal(GroupLifecycle.Disabled, disabledCommand.Status);
        Assert.Equal("maintenance", disabledCommand.Reason);
        Assert.Null(disabledCommand.UserAgent);

    }

    [Fact]
    public async Task AdminUpdateMapsArchivedLifecycleAndResponse()
    {
        await using GroupApiFactory factory = new();
        factory.UseCases.UpdateResult = Result.Success(new GroupCommandOutcome(
            StatusCodes.Status200OK,
            IsReplay: false,
            View(GroupLifecycle.Archived, version: 9, name: "Renamed"),
            "\"v9\""));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage archived = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { status = "archived", reason = "retention complete" },
            "application/merge-patch+json",
            "group-archive",
            "\"v8\"");
        using HttpResponseMessage archivedResponse = await client.SendAsync(
            archived,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, archivedResponse.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(archivedResponse).ConfigureAwait(true))
        {
            Assert.Equal("archived", document.RootElement.GetProperty("status").GetString());
        }
        Assert.Equal(
            GroupLifecycle.Archived,
            Assert.IsType<UpdateGroupCommand>(factory.UseCases.LastUpdateCommand).Status);

    }

    [Fact]
    public async Task AdminUpdateRoutesActiveLifecycleThroughTheOrchestrator()
    {
        await using GroupApiFactory factory = new();
        factory.Activation.Result = Result.Success(new GroupActivationResult(
            GroupId,
            GroupLifecycle.Active,
            Version: 10,
            new GroupResourceSnapshot(
                GroupId,
                "Activated",
                null,
                GroupLifecycle.Active,
                10,
                Timestamp,
                Timestamp.AddMinutes(2))));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage active = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new
            {
                name = "Activated",
                description = (string?)null,
                status = "active",
                reason = "supply ready",
            },
            "application/merge-patch+json",
            "group-activate",
            "\"v9\"");

        using HttpResponseMessage activeResponse = await client.SendAsync(
            active,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        Assert.Equal("\"v10\"", activeResponse.Headers.ETag?.Tag);
        using (JsonDocument document = await ReadJsonAsync(activeResponse).ConfigureAwait(true))
        {
            Assert.Equal("Activated", document.RootElement.GetProperty("name").GetString());
            Assert.Equal("active", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("description").ValueKind);
        }

        GroupActivationOrchestrationCommand activation =
            Assert.IsType<GroupActivationOrchestrationCommand>(factory.Activation.LastCommand);
        Assert.Equal(ActorId, activation.Actor.UserId);
        Assert.Equal(7, activation.Actor.TokenVersion);
        Assert.Equal(GroupId, activation.GroupId);
        Assert.Equal(9, activation.ExpectedGroupVersion);
        Assert.Equal("group-activate", activation.IdempotencyKey);
        Assert.Equal("supply ready", activation.Reason);
        GroupMetadataPatch metadata = Assert.IsType<GroupMetadataPatch>(activation.MetadataPatch);
        Assert.True(metadata.HasName);
        Assert.Equal("Activated", metadata.Name);
        Assert.True(metadata.HasDescription);
        Assert.Null(metadata.Description);
        Assert.NotNull(activation.RequestId);
    }

    [Fact]
    public async Task CreateTransportAndFieldBoundariesFailBeforeTheUseCase()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new { name = "Valid", total_tokens = 1 },
            "application/problem+json",
            "wrong-content");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);
        await AssertProblemAsync(wrongContentResponse, "unsupported_media_type")
            .ConfigureAwait(true);

        await AssertInvalidCreateFieldsAsync(client).ConfigureAwait(true);

        using HttpRequestMessage missingKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new { name = "Valid", total_tokens = 1 },
            idempotencyKey: null);
        using HttpResponseMessage missingKeyResponse = await client.SendAsync(
            missingKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingKeyResponse.StatusCode);
        await AssertProblemAsync(missingKeyResponse, "idempotency_key_required")
            .ConfigureAwait(true);

        foreach (string invalidKey in new[] { "contains space", new string('k', 129) })
        {
            using HttpRequestMessage invalid = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/groups",
                new { name = "Valid", total_tokens = 1 },
                idempotencyKey: invalidKey);
            using HttpResponseMessage invalidResponse = await client.SendAsync(
                invalid,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            await AssertProblemAsync(
                invalidResponse,
                "invalid_request",
                "/headers/Idempotency-Key").ConfigureAwait(true);
        }

        Assert.Equal(0, factory.UseCases.CreateCalls);
    }

    [Fact]
    public async Task UpdateTransportFieldAndPreconditionBoundariesFailBeforeTheUseCase()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { name = "Valid" },
            "application/json",
            "wrong-update-content",
            "\"v1\"");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);
        await AssertProblemAsync(wrongContentResponse, "unsupported_media_type")
            .ConfigureAwait(true);

        using HttpRequestMessage emptyId = JsonCommand(
            HttpMethod.Patch,
            "/api/v1/admin/groups/00000000-0000-0000-0000-000000000000",
            new { name = "Valid" },
            "application/merge-patch+json",
            "empty-id",
            "\"v1\"");
        using HttpResponseMessage emptyIdResponse = await client.SendAsync(
            emptyId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptyIdResponse.StatusCode);
        await AssertProblemAsync(emptyIdResponse, "invalid_request", "/groupId")
            .ConfigureAwait(true);

        await AssertInvalidUpdateFieldsAsync(client).ConfigureAwait(true);
        await AssertInvalidUpdateIdempotencyAsync(client).ConfigureAwait(true);
        await AssertInvalidIfMatchAsync(client).ConfigureAwait(true);

        Assert.Equal(0, factory.UseCases.UpdateCalls);
        Assert.Equal(0, factory.Activation.Calls);
    }

    [Fact]
    public async Task InvalidListAndEmptyGetIdentifiersReturnContractBadRequests()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        foreach (string limit in new[] { "0", "101", "not-a-number", "+1", "-1" })
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/api/v1/admin/groups?limit={Uri.EscapeDataString(limit)}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request", "/limit")
                .ConfigureAwait(true);
        }

        using HttpResponseMessage emptyId = await client.GetAsync(
            "/api/v1/admin/groups/00000000-0000-0000-0000-000000000000",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);
        await AssertProblemAsync(emptyId, "invalid_request", "/groupId")
            .ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.ListCalls);
        Assert.Equal(0, factory.UseCases.GetCalls);
    }

    [Fact]
    public async Task EveryGroupEndpointMapsApplicationFailuresWithoutLosingCurrentEtag()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        factory.UseCases.GetResult = Result.Failure<GroupView>(
            GroupErrorCodes.ResourceNotFound,
            "synthetic missing Group");
        using HttpResponseMessage get = await client.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        await AssertProblemAsync(get, "resource_not_found").ConfigureAwait(true);

        factory.UseCases.CreateResult = Result.Failure<GroupCommandOutcome>(
            GroupErrorCodes.ResourceConflict,
            "synthetic name conflict");
        using HttpRequestMessage createRequest = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new { name = "Conflict", total_tokens = 1 },
            idempotencyKey: "create-conflict");
        using HttpResponseMessage create = await client.SendAsync(
            createRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
        await AssertProblemAsync(create, "resource_conflict").ConfigureAwait(true);

        factory.UseCases.UpdateResult = Result.Failure<GroupCommandOutcome>(
            GroupErrorCodes.VersionConflict,
            "synthetic stale version",
            etag: "\"v17\"");
        using HttpRequestMessage updateRequest = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { name = "Stale" },
            "application/merge-patch+json",
            "update-stale",
            "\"v7\"");
        using HttpResponseMessage update = await client.SendAsync(
            updateRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, update.StatusCode);
        Assert.Equal("\"v17\"", update.Headers.ETag?.Tag);
        await AssertProblemAsync(update, "version_conflict", expectedRetryable: true)
            .ConfigureAwait(true);

        await AssertActivationFailureAsync(factory, client).ConfigureAwait(true);
    }

    [Fact]
    public async Task FrozenApplicationErrorsMapToTheirCanonicalHttpPresentation()
    {
        await using GroupApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        (string SourceCode, HttpStatusCode Status, string ResponseCode, bool Retryable,
            bool HasRetryAfter, string? ETag)[] cases =
        [
            ("role_required", HttpStatusCode.Forbidden, "role_required", false, false, null),
            ("forbidden", HttpStatusCode.Forbidden, "role_required", false, false, null),
            ("resource_not_found", HttpStatusCode.NotFound, "resource_not_found", false, false, null),
            ("idempotency_conflict", HttpStatusCode.Conflict, "idempotency_conflict", false, false, null),
            ("resource_conflict", HttpStatusCode.Conflict, "resource_conflict", false, false, null),
            ("group_activation_not_ready", HttpStatusCode.Conflict, "group_activation_not_ready", false, false, null),
            ("version_conflict", HttpStatusCode.PreconditionFailed, "version_conflict", true, false, "\"v23\""),
            ("validation_failed", HttpStatusCode.UnprocessableEntity, "validation_failed", false, false, null),
            ("invalid_request", HttpStatusCode.BadRequest, "invalid_request", false, false, null),
            ("rate_limit_exceeded", HttpStatusCode.TooManyRequests, "rate_limit_exceeded", true, true, null),
            ("coordination_unavailable", HttpStatusCode.ServiceUnavailable, "coordination_unavailable", true, true, null),
            ("dependency_unavailable", HttpStatusCode.ServiceUnavailable, "dependency_unavailable", true, true, null),
            ("service_unavailable", HttpStatusCode.ServiceUnavailable, "dependency_unavailable", true, true, null),
            ("internal_error", HttpStatusCode.InternalServerError, "internal_error", false, false, null),
        ];

        foreach ((string sourceCode, HttpStatusCode status, string responseCode,
                     bool retryable, bool hasRetryAfter, string? etag) in cases)
        {
            factory.UseCases.ListResult = Result.Failure<GroupPage>(
                sourceCode,
                "synthetic failure",
                retryAfterSeconds: string.Equals(
                    sourceCode,
                    GroupErrorCodes.RateLimitExceeded,
                    StringComparison.Ordinal)
                        ? 1
                        : null,
                etag: etag);
            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/admin/groups",
                TestContext.Current.CancellationToken);

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(etag, response.Headers.ETag?.Tag);
            Assert.Equal(
                hasRetryAfter ? TimeSpan.FromSeconds(1) : null,
                response.Headers.RetryAfter?.Delta);
            await AssertProblemAsync(response, responseCode, expectedRetryable: retryable)
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task FrozenApplicationPresentationCarriesFieldErrorsWithoutReclassification()
    {
        await using GroupApiFactory factory = new();
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/cursor"] = ["The cursor is no longer valid."],
            };
        factory.UseCases.ListResult = Result.Failure<GroupPage>(
            GroupErrorCodes.ValidationFailed,
            "synthetic cursor failure",
            presentation: new ResultErrorPresentation(
                GroupErrorCodes.ValidationFailed,
                StatusCodes.Status422UnprocessableEntity,
                "Cursor validation failed",
                "The supplied cursor cannot be resumed.",
                Retryable: false,
                Errors: errors));
        using HttpClient client = AuthenticatedClient(factory, "admin");

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/groups?cursor=stale",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true);
        Assert.Equal(
            "Cursor validation failed",
            document.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The supplied cursor cannot be resumed.",
            document.RootElement.GetProperty("detail").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty(
            "/cursor",
            out JsonElement messages));
        Assert.Equal("The cursor is no longer valid.", messages[0].GetString());
    }

    [Fact]
    public async Task ActivationSuccessWithoutResourceSnapshotFailsClosed()
    {
        await using GroupApiFactory factory = new();
        factory.Activation.Result = Result.Success(new GroupActivationResult(
            GroupId,
            GroupLifecycle.Active,
            Version: 8));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { status = "active", reason = "activate" },
            "application/merge-patch+json",
            "activation-missing-snapshot",
            "\"v7\"");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertProblemAsync(response, "internal_error").ConfigureAwait(true);
    }

    [Fact]
    public async Task RuntimePoliciesRejectUserWritesAndMissingAuthentication()
    {
        await using GroupApiFactory factory = new();
        using HttpClient user = AuthenticatedClient(factory, "user");
        using HttpRequestMessage write = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            new { name = "Forbidden", total_tokens = 1 },
            idempotencyKey: "forbidden-write");
        using HttpResponseMessage forbidden = await user.SendAsync(
            write,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertProblemAsync(forbidden, "role_required").ConfigureAwait(true);

        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthorized = await anonymous.GetAsync(
            "/api/v1/admin/groups",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains(
            unauthorized.Headers.WwwAuthenticate,
            static header => string.Equals(header.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(unauthorized, "authentication_required").ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.CreateCalls);
    }

    [Fact]
    public async Task EndpointFilterRejectsAnAuthenticatedPrincipalWithInvalidGroupClaims()
    {
        await using GroupEndpointFilterApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role: null,
                tokenVersion: 7,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: ActorId.Value,
                roleClaims: ["owner", "admin"]));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/groups",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static header => string.Equals(header.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "invalid_user_token").ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.ListCalls);
    }

    [Fact]
    public async Task EndpointFilterParsesUserRoleBeforeTheReadUseCase()
    {
        await using GroupEndpointFilterApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role: null,
                tokenVersion: 7,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: ActorId.Value,
                roleClaims: ["user", "admin"]));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/groups",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ListGroupsQuery query = Assert.IsType<ListGroupsQuery>(factory.UseCases.LastListQuery);
        Assert.Equal(GroupControlRole.User, query.Actor.Role);
    }

    private static async ValueTask AssertLifecyclePageAsync(HttpResponseMessage response)
    {
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(false);
        JsonElement[] data = document.RootElement.GetProperty("data")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, data.Length);
        Assert.Equal("active", data[0].GetProperty("status").GetString());
        Assert.Equal("disabled", data[1].GetProperty("status").GetString());
        Assert.Equal("archived", data[2].GetProperty("status").GetString());
        Assert.All(data, static group =>
            Assert.Equal("openai", group.GetProperty("platform").GetString()));
        JsonElement page = document.RootElement.GetProperty("page");
        Assert.True(page.GetProperty("has_more").GetBoolean());
        Assert.Equal("next/page", page.GetProperty("next_cursor").GetString());
    }

    private static async ValueTask AssertInvalidCreateFieldsAsync(HttpClient client)
    {
        (string Name, string? Description, long TotalTokens, string Pointer)[] cases =
        [
            (" ", null, 1, "/name"),
            (new string('n', 101), null, 1, "/name"),
            ("invalid\u0001name", null, 1, "/name"),
            ("Valid", new string('d', 1001), 1, "/description"),
            ("Valid", null, 0, "/total_tokens"),
            ("Valid", null, 9_007_199_254_740_992, "/total_tokens"),
        ];
        foreach ((string name, string? description, long totalTokens, string pointer) in cases)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/groups",
                new { name, description, total_tokens = totalTokens });
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertInvalidQuotaMutationFieldsAsync(
        HttpClient client,
        string operation,
        string tokenProperty)
    {
        string path =
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/{operation}";
        (string Body, string Pointer)[] cases =
        [
            (QuotaMutationBody(tokenProperty, "\"1\""), $"/{tokenProperty}"),
            (QuotaMutationBody(tokenProperty, "1.5"), $"/{tokenProperty}"),
            (QuotaMutationBody(tokenProperty, "1e-1"), $"/{tokenProperty}"),
            (QuotaMutationBody(tokenProperty, "100e-3"), $"/{tokenProperty}"),
            (QuotaMutationBody(tokenProperty, "0"), $"/{tokenProperty}"),
            (QuotaMutationBody(tokenProperty, "-1"), $"/{tokenProperty}"),
            (
                QuotaMutationBody(tokenProperty, "9007199254740992"),
                $"/{tokenProperty}"),
            (
                $$"""{"{{tokenProperty}}":1,"reason":"   "}""",
                "/reason"),
            (
                $$"""{"{{tokenProperty}}":1,"reason":"\uD800"}""",
                "/reason"),
            (
                $$"""{"{{tokenProperty}}":1,"reason":"valid","\uD800":true}""",
                "/"),
            (
                $$"""{"{{tokenProperty}}":1,"reason":"valid","unexpected":true}""",
                "/unexpected"),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            using HttpRequestMessage request = RawJsonCommand(
                cases[index].Body,
                $"quota-{operation}-invalid-field-{index}",
                path,
                ifMatch: "\"v1\"");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(false);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(
                response,
                "validation_failed",
                cases[index].Pointer).ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertQuotaMutationTransportFailuresAsync(
        HttpClient client,
        string operation,
        string tokenProperty)
    {
        string validBody = QuotaMutationBody(tokenProperty, "1");
        string path =
            $"/api/v1/admin/groups/{GroupId.Value:D}/quota/{operation}";
        using (HttpRequestMessage wrongMedia = RawJsonCommand(
                   validBody,
                   $"quota-{operation}-wrong-media",
                   path,
                   contentType: "application/merge-patch+json",
                   ifMatch: "\"v1\""))
        using (HttpResponseMessage response = await client.SendAsync(
                   wrongMedia,
                   TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            await AssertProblemAsync(response, "unsupported_media_type")
                .ConfigureAwait(false);
        }

        using (HttpRequestMessage emptyPath = RawJsonCommand(
                   validBody,
                   $"quota-{operation}-empty-path",
                   $"/api/v1/admin/groups/{Guid.Empty:D}/quota/{operation}",
                   ifMatch: "\"v1\""))
        using (HttpResponseMessage response = await client.SendAsync(
                   emptyPath,
                   TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request", "/groupId")
                .ConfigureAwait(false);
        }

        await AssertInvalidQuotaMutationIdempotencyHeadersAsync(
            client,
            validBody,
            path).ConfigureAwait(false);

        using (HttpRequestMessage missingIfMatch = RawJsonCommand(
                   validBody,
                   $"quota-{operation}-missing-if-match",
                   path))
        using (HttpResponseMessage response = await client.SendAsync(
                   missingIfMatch,
                   TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
            await AssertProblemAsync(response, "if_match_required")
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertInvalidQuotaMutationIdempotencyHeadersAsync(
        HttpClient client,
        string validBody,
        string path)
    {
        using (HttpRequestMessage missing = RawJsonCommand(
                   validBody,
                   idempotencyKey: null,
                   path: path,
                   ifMatch: "\"v1\""))
        using (HttpResponseMessage response = await client.SendAsync(
                   missing,
                   TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
            await AssertProblemAsync(response, "idempotency_key_required")
                .ConfigureAwait(false);
        }

        using (HttpRequestMessage empty = RawJsonCommand(
                   validBody,
                   idempotencyKey: string.Empty,
                   path: path,
                   ifMatch: "\"v1\""))
        using (HttpResponseMessage response = await client.SendAsync(
                   empty,
                   TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
            await AssertProblemAsync(response, "idempotency_key_required")
                .ConfigureAwait(false);
        }

        using HttpRequestMessage multiple = RawJsonCommand(
            validBody,
            idempotencyKey: null,
            path: path,
            ifMatch: "\"v1\"");
        multiple.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            MultipleIdempotencyKeys);
        using HttpResponseMessage multipleResponse = await client.SendAsync(
            multiple,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.BadRequest, multipleResponse.StatusCode);
        await AssertProblemAsync(
            multipleResponse,
            "invalid_request",
            "/headers/Idempotency-Key").ConfigureAwait(false);
    }

    private static string QuotaMutationBody(
        string tokenProperty,
        string rawTokenValue) =>
        $$"""{"{{tokenProperty}}":{{rawTokenValue}},"reason":"valid reason"}""";

    private static async ValueTask AssertInvalidUpdateFieldsAsync(HttpClient client)
    {
        (object Body, string Pointer)[] cases =
        [
            (new { }, "/"),
            (new { name = " " }, "/name"),
            (new { name = new string('n', 101) }, "/name"),
            (new { name = "invalid\u0001name" }, "/name"),
            (new { name = "Valid", description = new string('d', 1001) }, "/description"),
            (new { status = 999, reason = "valid reason" }, "/status"),
            (new { status = "disabled" }, "/reason"),
            (new { status = "disabled", reason = "bad\nreason" }, "/reason"),
            (new { name = "Valid", reason = " " }, "/reason"),
        ];
        foreach ((object body, string pointer) in cases)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Patch,
                $"/api/v1/admin/groups/{GroupId.Value:D}",
                body,
                "application/merge-patch+json");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertInvalidUpdateIdempotencyAsync(HttpClient client)
    {
        using HttpRequestMessage missing = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { name = "Valid" },
            "application/merge-patch+json",
            ifMatch: "\"v1\"");
        using HttpResponseMessage missingResponse = await client.SendAsync(
            missing,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingResponse.StatusCode);
        await AssertProblemAsync(missingResponse, "idempotency_key_required")
            .ConfigureAwait(false);

        using HttpRequestMessage invalid = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { name = "Valid" },
            "application/merge-patch+json",
            "invalid key",
            "\"v1\"");
        using HttpResponseMessage invalidResponse = await client.SendAsync(
            invalid,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        await AssertProblemAsync(
            invalidResponse,
            "invalid_request",
            "/headers/Idempotency-Key").ConfigureAwait(false);
    }

    private static async ValueTask AssertInvalidIfMatchAsync(HttpClient client)
    {
        using HttpRequestMessage missing = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { name = "Valid" },
            "application/merge-patch+json",
            "missing-if-match");
        using HttpResponseMessage missingResponse = await client.SendAsync(
            missing,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingResponse.StatusCode);
        await AssertProblemAsync(missingResponse, "if_match_required").ConfigureAwait(false);

        foreach (string etag in new[]
                 {
                     "v1", "W/\"v1\"", "\"v0\"", "\"v01\"", "\"v\"",
                 })
        {
            using HttpRequestMessage invalid = JsonCommand(
                HttpMethod.Patch,
                $"/api/v1/admin/groups/{GroupId.Value:D}",
                new { name = "Valid" },
                "application/merge-patch+json",
                "invalid-if-match",
                etag);
            using HttpResponseMessage response = await client.SendAsync(
                invalid,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request", "/headers/If-Match")
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask AssertActivationFailureAsync(
        GroupApiFactory factory,
        HttpClient client)
    {
        factory.Activation.Result = Result.Failure<GroupActivationResult>(
            GroupErrorCodes.GroupActivationNotReady,
            "synthetic Supply readiness failure");
        using HttpRequestMessage request = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}",
            new { status = "active", reason = "activate" },
            "application/merge-patch+json",
            "activate-not-ready",
            "\"v7\"");
        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemAsync(response, "group_activation_not_ready").ConfigureAwait(false);
    }

    private static HttpRequestMessage JsonCommand(
        HttpMethod method,
        string path,
        object body,
        string contentType = "application/json",
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        HttpRequestMessage request = new(method, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static HttpRequestMessage RawJsonCommand(
        string body,
        string? idempotencyKey,
        string path = "/api/v1/admin/groups",
        string contentType = "application/json",
        string? ifMatch = null)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            path)
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                contentType),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation(
                "Idempotency-Key",
                idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return request;
    }

    private static HttpClient AuthenticatedClient(GroupApiFactory factory, string role)
    {
        factory.AccessSessionValidator.CanonicalRole = role switch
        {
            "admin" => SystemRole.Admin,
            "operator" => SystemRole.Operator,
            "auditor" => SystemRole.Auditor,
            "user" => SystemRole.User,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role,
                tokenVersion: 7,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: ActorId.Value));
        return client;
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(false));

    private static string AssertRequestId(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(
            "X-Request-Id",
            out IEnumerable<string>? values));
        string requestId = Assert.Single(values);
        Assert.True(Guid.TryParse(requestId, out _));
        return requestId;
    }

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        string expectedCode,
        string? expectedErrorPointer = null,
        bool expectedRetryable = false)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string requestId = AssertRequestId(response);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(false);
        JsonElement problem = document.RootElement;
        Assert.Equal(requestId, problem.GetProperty("request_id").GetString());
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
        Assert.Equal(expectedRetryable, problem.GetProperty("retryable").GetBoolean());
        if (expectedErrorPointer is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(
                expectedErrorPointer,
                out JsonElement messages));
            Assert.NotEmpty(messages.EnumerateArray());
        }
    }

    private static string UserAgentDigest(string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(value[..Math.Min(value.Length, 512)]);
        byte[] digest = SHA256.HashData(input);
        try
        {
            return string.Concat("sha256:", Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static GroupView View(
        GroupLifecycle lifecycle,
        long version,
        string name,
        string? description = "description") => new(
        GroupId,
        name,
        description,
        lifecycle,
        version,
        Timestamp,
        Timestamp.AddMinutes(version));

    private static GroupQuotaView LargeQuotaView(
        GroupPoolQuotaStatus status,
        long version) => new(
        GroupId,
        QuotaPeriodId,
        status,
        BigInteger.Parse(QuotaTotal, CultureInfo.InvariantCulture),
        BigInteger.Parse(QuotaConsumed, CultureInfo.InvariantCulture),
        BigInteger.Parse(QuotaReserved, CultureInfo.InvariantCulture),
        BigInteger.Zero,
        BigInteger.One,
        Timestamp.AddDays(-1),
        null,
        version,
        Timestamp);

    private static GroupQuotaView ActiveQuotaView(
        long totalTokens,
        long version) => new(
        GroupId,
        QuotaPeriodId,
        GroupPoolQuotaStatus.Active,
        new BigInteger(totalTokens),
        BigInteger.Zero,
        BigInteger.Zero,
        new BigInteger(totalTokens),
        BigInteger.Zero,
        Timestamp,
        null,
        version,
        Timestamp);

    private class GroupApiFactory : PoolAiApiFactory
    {
        internal FakeGroupUseCases UseCases { get; } = new();

        internal FakeActivationOrchestrator Activation { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IListGroupsUseCase>();
                services.RemoveAll<IGetGroupUseCase>();
                services.RemoveAll<ICreateGroupUseCase>();
                services.RemoveAll<IUpdateGroupUseCase>();
                services.RemoveAll<IGetGroupQuotaUseCase>();
                services.RemoveAll<IAuthorizeQuotaMutationUseCase>();
                services.RemoveAll<IAdjustGroupQuotaUseCase>();
                services.RemoveAll<IResetGroupQuotaUseCase>();
                services.RemoveAll<IGroupActivationOrchestrator>();
                services.AddSingleton<IListGroupsUseCase>(UseCases);
                services.AddSingleton<IGetGroupUseCase>(UseCases);
                services.AddSingleton<ICreateGroupUseCase>(UseCases);
                services.AddSingleton<IUpdateGroupUseCase>(UseCases);
                services.AddSingleton<IGetGroupQuotaUseCase>(UseCases);
                services.AddSingleton<IAuthorizeQuotaMutationUseCase>(UseCases);
                services.AddSingleton<IAdjustGroupQuotaUseCase>(UseCases);
                services.AddSingleton<IResetGroupQuotaUseCase>(UseCases);
                services.AddSingleton<IGroupActivationOrchestrator>(Activation);
            });
        }
    }

    private sealed class GroupEndpointFilterApiFactory : GroupApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    static options =>
                    {
                        options.EventsType = null;
                        options.Events = new JwtBearerEvents();
                    }));
        }
    }

    private sealed class FakeGroupUseCases :
        IListGroupsUseCase,
        IGetGroupUseCase,
        ICreateGroupUseCase,
        IUpdateGroupUseCase,
        IGetGroupQuotaUseCase,
        IAuthorizeQuotaMutationUseCase,
        IAdjustGroupQuotaUseCase,
        IResetGroupQuotaUseCase
    {
        internal Result<GroupPage> ListResult { get; set; } = Result.Success(
            new GroupPage([View(GroupLifecycle.Disabled, 1, "Default")], null, false));

        internal Result<GroupView> GetResult { get; set; } = Result.Success(
            View(GroupLifecycle.Disabled, 1, "Default"));

        internal Result<GroupCommandOutcome> CreateResult { get; set; } = Result.Success(
            new GroupCommandOutcome(
                StatusCodes.Status201Created,
                false,
                View(GroupLifecycle.Disabled, 1, "Default"),
                "\"v1\""));

        internal Result<GroupCommandOutcome> UpdateResult { get; set; } = Result.Success(
            new GroupCommandOutcome(
                StatusCodes.Status200OK,
                false,
                View(GroupLifecycle.Disabled, 2, "Default"),
                "\"v2\""));

        internal Result<GroupQuotaView> GetQuotaResult { get; set; } =
            Result.Success(LargeQuotaView(GroupPoolQuotaStatus.Exhausted, 1));

        internal Result<GroupQuotaCommandOutcome> AdjustQuotaResult { get; set; } =
            Result.Success(new GroupQuotaCommandOutcome(
                StatusCodes.Status200OK,
                false,
                LargeQuotaView(GroupPoolQuotaStatus.Exhausted, 2),
                "\"v2\""));

        internal Result<GroupQuotaCommandOutcome> ResetQuotaResult { get; set; } =
            Result.Success(new GroupQuotaCommandOutcome(
                StatusCodes.Status200OK,
                false,
                ActiveQuotaView(totalTokens: 1, version: 2),
                "\"v2\""));

        internal bool ReturnInvalidQuotaAuthorizationSuccess { get; set; }

        internal ListGroupsQuery? LastListQuery { get; private set; }

        internal GetGroupQuery? LastGetQuery { get; private set; }

        internal CreateGroupCommand? LastCreateCommand { get; private set; }

        internal UpdateGroupCommand? LastUpdateCommand { get; private set; }

        internal GetGroupQuotaQuery? LastGetQuotaQuery { get; private set; }

        internal AuthorizeQuotaMutationCommand? LastQuotaAuthorizationCommand
        {
            get;
            private set;
        }

        internal AdjustGroupQuotaCommand? LastAdjustQuotaCommand { get; private set; }

        internal ResetGroupQuotaCommand? LastResetQuotaCommand { get; private set; }

        internal int ListCalls { get; private set; }

        internal int GetCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int UpdateCalls { get; private set; }

        internal int GetQuotaCalls { get; private set; }

        internal int QuotaAuthorizationCalls { get; private set; }

        internal int AdjustQuotaCalls { get; private set; }

        internal int ResetQuotaCalls { get; private set; }

        public ValueTask<Result<GroupPage>> ExecuteAsync(
            ListGroupsQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            LastListQuery = query;
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<Result<GroupView>> ExecuteAsync(
            GetGroupQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            LastGetQuery = query;
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
            CreateGroupCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreateCommand = command;
            return ValueTask.FromResult(CreateResult);
        }

        public ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
            UpdateGroupCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCalls++;
            LastUpdateCommand = command;
            return ValueTask.FromResult(UpdateResult);
        }

        public ValueTask<Result<GroupQuotaView>> ExecuteAsync(
            GetGroupQuotaQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetQuotaCalls++;
            LastGetQuotaQuery = query;
            return ValueTask.FromResult(GetQuotaResult);
        }

        public ValueTask<Result<bool>> ExecuteAsync(
            AuthorizeQuotaMutationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaAuthorizationCalls++;
            LastQuotaAuthorizationCommand = command;
            if (ReturnInvalidQuotaAuthorizationSuccess)
            {
                return ValueTask.FromResult(Result.Success(false));
            }

            return ValueTask.FromResult(
                command.Actor.Role == GroupControlRole.Admin
                    ? Result.Success(true)
                    : Result.Failure<bool>(
                        GroupErrorCodes.RoleRequired,
                        "The Admin role is required for quota mutations."));
        }

        public ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
            AdjustGroupQuotaCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdjustQuotaCalls++;
            LastAdjustQuotaCommand = command;
            return ValueTask.FromResult(
                command.Actor.Role == GroupControlRole.Admin
                    ? AdjustQuotaResult
                    : RoleRequiredQuotaMutation());
        }

        public ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
            ResetGroupQuotaCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetQuotaCalls++;
            LastResetQuotaCommand = command;
            return ValueTask.FromResult(
                command.Actor.Role == GroupControlRole.Admin
                    ? ResetQuotaResult
                    : RoleRequiredQuotaMutation());
        }

        private static Result<GroupQuotaCommandOutcome>
            RoleRequiredQuotaMutation() =>
            Result.Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.RoleRequired,
                "The Admin role is required for quota mutations.");
    }

    private sealed class FakeActivationOrchestrator : IGroupActivationOrchestrator
    {
        internal Result<GroupActivationResult> Result { get; set; } =
            PoolAI.BuildingBlocks.Result.Success(new GroupActivationResult(
                GroupId,
                GroupLifecycle.Active,
                Version: 2,
                new GroupResourceSnapshot(
                    GroupId,
                    "Default",
                    "description",
                    GroupLifecycle.Active,
                    2,
                    Timestamp,
                    Timestamp.AddMinutes(1))));

        internal GroupActivationOrchestrationCommand? LastCommand { get; private set; }

        internal int Calls { get; private set; }

        public ValueTask<Result<GroupActivationResult>> ActivateAsync(
            GroupActivationOrchestrationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastCommand = command;
            return ValueTask.FromResult(Result);
        }
    }
}
