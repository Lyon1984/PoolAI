#pragma warning disable MA0051 // Contract scenarios intentionally keep complete HTTP proofs together.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.EndToEndTests;

public sealed class ApiKeyEndpointContractTests
{
    private static readonly EntityId ActorId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0636100");
    private static readonly EntityId TargetUserId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0636101");
    private static readonly EntityId ApiKeyId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0636102");
    private static readonly EntityId GroupId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0636103");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-07-23T08:00:00Z",
        CultureInfo.InvariantCulture);
    private static readonly string[] ValidInputCidrs =
        ["10.1.2.3/24", "2001:DB8::1/64"];
    private static readonly string[] InvalidInputCidrs = ["not-a-cidr"];
    private static readonly string[] ExpectedOperationNames =
    [
        "adminCreateUserApiKey",
        "adminGetUserApiKey",
        "adminListUserApiKeys",
        "createMyApiKey",
        "getMyApiKey",
        "listMyApiKeys",
    ];

    [Fact]
    public async Task SelfReadRoutesDeriveTargetFromCanonicalActorAndNeverReturnSecret()
    {
        await using ApiKeyApiFactory factory = new();
        factory.Port.ListResult = Result.Success(new PoolAI.Modules.Identity.Abstractions.ApiKeyPage(
            [Snapshot(ActorId, version: 7)],
            "next/api-key/page",
            HasMore: true));
        factory.Port.GetResult = Result.Success(Snapshot(ActorId, version: 8));
        using HttpClient client = AuthenticatedClient(factory, "user");

        using HttpResponseMessage list = await client.GetAsync(
            "/api/v1/me/api-keys?cursor=prior%2Fpage&limit=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("application/json", list.Content.Headers.ContentType?.MediaType);
        AssertRequestId(list);
        using (JsonDocument document = await ReadJsonAsync(list).ConfigureAwait(true))
        {
            JsonElement item = Assert.Single(
                document.RootElement.GetProperty("data").EnumerateArray().ToArray());
            Assert.Equal(ApiKeyId.Value, item.GetProperty("id").GetGuid());
            Assert.Equal("active", item.GetProperty("status").GetString());
            Assert.Equal("active", item.GetProperty("effective_status").GetString());
            Assert.Equal("10.0.0.0/24", Assert.Single(
                item.GetProperty("allowed_cidrs").EnumerateArray()).GetString());
            Assert.False(item.TryGetProperty("secret", out _));
            JsonElement page = document.RootElement.GetProperty("page");
            Assert.True(page.GetProperty("has_more").GetBoolean());
            Assert.Equal("next/api-key/page", page.GetProperty("next_cursor").GetString());
        }

        ListApiKeysQuery listQuery = Assert.IsType<ListApiKeysQuery>(factory.Port.LastListQuery);
        Assert.Equal(ActorId, listQuery.Actor.UserId);
        Assert.Equal(SystemRole.User, listQuery.Actor.Role);
        Assert.Equal(7, listQuery.Actor.TokenVersion);
        Assert.Equal(ApiKeyAccessMode.Self, listQuery.AccessMode);
        Assert.Equal(ActorId, listQuery.UserId);
        Assert.Equal("prior/page", listQuery.Cursor);
        Assert.Equal(100, listQuery.Limit);

        using HttpResponseMessage get = await client.GetAsync(
            $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v8\"", get.Headers.ETag?.Tag);
        AssertRequestId(get);
        using (JsonDocument document = await ReadJsonAsync(get).ConfigureAwait(true))
        {
            Assert.False(document.RootElement.TryGetProperty("secret", out _));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("expires_at").ValueKind);
        }

        GetApiKeyQuery getQuery = Assert.IsType<GetApiKeyQuery>(factory.Port.LastGetQuery);
        Assert.Equal(ApiKeyAccessMode.Self, getQuery.AccessMode);
        Assert.Equal(ActorId, getQuery.UserId);
        Assert.Equal(ApiKeyId, getQuery.ApiKeyId);
    }

    [Fact]
    public async Task AdminReadRoutesUseThePathTargetAndRequireTheAdminRole()
    {
        await using ApiKeyApiFactory factory = new();
        factory.Port.ListResult = Result.Success(new PoolAI.Modules.Identity.Abstractions.ApiKeyPage(
            [Snapshot(TargetUserId, version: 4)],
            null,
            HasMore: false));
        factory.Port.GetResult = Result.Success(Snapshot(TargetUserId, version: 5));
        using HttpClient admin = AuthenticatedClient(factory, "admin");

        using HttpResponseMessage list = await admin.GetAsync(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage get = await admin.GetAsync(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys/{ApiKeyId.Value:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v5\"", get.Headers.ETag?.Tag);
        ListApiKeysQuery listQuery = Assert.IsType<ListApiKeysQuery>(factory.Port.LastListQuery);
        Assert.Equal(ApiKeyAccessMode.AdminProxy, listQuery.AccessMode);
        Assert.Equal(TargetUserId, listQuery.UserId);
        Assert.Equal(ActorId, listQuery.Actor.UserId);
        Assert.Equal(50, listQuery.Limit);
        GetApiKeyQuery getQuery = Assert.IsType<GetApiKeyQuery>(factory.Port.LastGetQuery);
        Assert.Equal(ApiKeyAccessMode.AdminProxy, getQuery.AccessMode);
        Assert.Equal(TargetUserId, getQuery.UserId);

        int callsBeforeForbiddenRequest = factory.Port.ListCalls;
        using HttpClient user = AuthenticatedClient(factory, "user");
        using HttpResponseMessage forbidden = await user.GetAsync(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertProblemAsync(forbidden, "role_required").ConfigureAwait(true);
        Assert.Equal(callsBeforeForbiddenRequest, factory.Port.ListCalls);
    }

    [Fact]
    public async Task SelfCreateMapsTheGeneratedRequestAndReturnsBoundSecretHeaders()
    {
        await using ApiKeyApiFactory factory = new();
        factory.Port.CreateResult = Result.Success(Created(
            ActorId,
            $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
            "automation",
            Timestamp.AddDays(30),
            ["10.1.2.0/24", "2001:db8::/64"]));
        using HttpClient client = AuthenticatedClient(factory, "auditor");
        DateTimeOffset expiresAt = Timestamp.AddDays(30);
        string userAgent = new('a', 600);
        using HttpRequestMessage request = JsonCommand(
            $"/api/v1/me/api-keys",
            new
            {
                name = "automation",
                group_id = GroupId.Value,
                expires_at = expiresAt,
                allowed_cidrs = ValidInputCidrs,
            },
            "self-create-key");
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
            response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string requestId = AssertRequestId(response);
        using (JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true))
        {
            Assert.Equal(TestSecret, document.RootElement.GetProperty("secret").GetString());
            Assert.Equal(
                ApiKeyId.Value,
                document.RootElement.GetProperty("api_key").GetProperty("id").GetGuid());
            Assert.False(
                document.RootElement.GetProperty("api_key").TryGetProperty("hash", out _));
        }

        CreateApiKeyCommand command = Assert.IsType<CreateApiKeyCommand>(
            factory.Port.LastCreateCommand);
        Assert.Equal(Guid.Parse(requestId), command.RequestId.Value);
        Assert.Equal(ActorId, command.Actor.UserId);
        Assert.Equal(SystemRole.Auditor, command.Actor.Role);
        Assert.Equal(ApiKeyAccessMode.Self, command.AccessMode);
        Assert.Equal(ActorId, command.UserId);
        Assert.Equal(GroupId, command.GroupId);
        Assert.Equal("self-create-key", command.IdempotencyKey);
        Assert.Equal("automation", command.Name);
        Assert.Equal(expiresAt, command.ExpiresAt);
        Assert.Equal(["10.1.2.3/24", "2001:DB8::1/64"], command.AllowedCidrs);
        Assert.Null(command.Reason);
        Assert.Equal(UserAgentDigest(userAgent), command.UserAgent);
    }

    [Fact]
    public async Task AdminCreateUsesThePathTargetAndCarriesTheRequiredAuditReason()
    {
        await using ApiKeyApiFactory factory = new();
        factory.Port.CreateResult = Result.Success(Created(
            TargetUserId,
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys/{ApiKeyId.Value:D}",
            "admin-created"));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = JsonCommand(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            new
            {
                name = "admin-created",
                group_id = GroupId.Value,
                allowed_cidrs = Array.Empty<string>(),
                reason = "approved operational access",
            },
            "admin-create-key");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.NoStore);
        CreateApiKeyCommand command = Assert.IsType<CreateApiKeyCommand>(
            factory.Port.LastCreateCommand);
        Assert.Equal(ApiKeyAccessMode.AdminProxy, command.AccessMode);
        Assert.Equal(ActorId, command.Actor.UserId);
        Assert.Equal(TargetUserId, command.UserId);
        Assert.Equal("approved operational access", command.Reason);
        Assert.Empty(command.AllowedCidrs);
    }

    [Fact]
    public async Task CreateTextScalarBoundariesAndLegalEdgeWhitespaceArePreservedExactly()
    {
        await using ApiKeyApiFactory factory = new();
        string name = "\u00a0"
            + string.Concat(Enumerable.Repeat("\U0001f600", 98))
            + "\u3000";
        string reason = "\u00a0"
            + string.Concat(Enumerable.Repeat("\U0001f600", 498))
            + "\u3000";
        factory.Port.CreateResult = Result.Success(Created(
            TargetUserId,
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys/{ApiKeyId.Value:D}",
            name));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage accepted = JsonCommand(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            new
            {
                name,
                group_id = GroupId.Value,
                reason,
            },
            "scalar-boundary-accepted");

        using HttpResponseMessage acceptedResponse = await client.SendAsync(
            accepted,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, acceptedResponse.StatusCode);
        CreateApiKeyCommand acceptedCommand = Assert.IsType<CreateApiKeyCommand>(
            factory.Port.LastCreateCommand);
        Assert.Equal(name, acceptedCommand.Name);
        Assert.Equal(reason, acceptedCommand.Reason);
        using (JsonDocument document =
               await ReadJsonAsync(acceptedResponse).ConfigureAwait(true))
        {
            Assert.Equal(
                name,
                document.RootElement
                    .GetProperty("api_key")
                    .GetProperty("name")
                    .GetString());
        }

        using HttpRequestMessage longName = JsonCommand(
            "/api/v1/me/api-keys",
            new
            {
                name = string.Concat(Enumerable.Repeat("\U0001f600", 101)),
                group_id = GroupId.Value,
            },
            "scalar-boundary-long-name");
        using HttpResponseMessage longNameResponse = await client.SendAsync(
            longName,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            longNameResponse.StatusCode);
        await AssertProblemAsync(
            longNameResponse,
            "validation_failed",
            "/name").ConfigureAwait(true);

        using HttpRequestMessage longReason = JsonCommand(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            new
            {
                name = "valid",
                group_id = GroupId.Value,
                reason = string.Concat(
                    Enumerable.Repeat("\U0001f600", 501)),
            },
            "scalar-boundary-long-reason");
        using HttpResponseMessage longReasonResponse = await client.SendAsync(
            longReason,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            longReasonResponse.StatusCode);
        await AssertProblemAsync(
            longReasonResponse,
            "validation_failed",
            "/reason").ConfigureAwait(true);
        Assert.Equal(1, factory.Port.CreateCalls);
    }

    [Fact]
    public async Task PaginationCreateHeadersAndBodiesFailCanonicallyBeforeTheirPorts()
    {
        await using ApiKeyApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        using HttpResponseMessage badCursor = await client.GetAsync(
            "/api/v1/me/api-keys?cursor=",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
        await AssertProblemAsync(badCursor, "invalid_request", "/cursor").ConfigureAwait(true);

        using HttpResponseMessage badLimit = await client.GetAsync(
            "/api/v1/me/api-keys?limit=101",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, badLimit.StatusCode);
        await AssertProblemAsync(badLimit, "invalid_request", "/limit").ConfigureAwait(true);

        using HttpRequestMessage missingKey = JsonCommand(
            "/api/v1/me/api-keys",
            new { name = "valid", group_id = GroupId.Value },
            idempotencyKey: null);
        using HttpResponseMessage missingKeyResponse = await client.SendAsync(
            missingKey,
            TestContext.Current.CancellationToken);
        Assert.Equal((HttpStatusCode)428, missingKeyResponse.StatusCode);
        await AssertProblemAsync(
            missingKeyResponse,
            "idempotency_key_required").ConfigureAwait(true);

        using HttpRequestMessage invalidBody = JsonCommand(
            $"/api/v1/admin/users/{TargetUserId.Value:D}/api-keys",
            new
            {
                name = " ",
                group_id = Guid.Empty,
                allowed_cidrs = InvalidInputCidrs,
                reason = " ",
            },
            "invalid-body");
        using HttpResponseMessage invalidBodyResponse = await client.SendAsync(
            invalidBody,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidBodyResponse.StatusCode);
        await AssertProblemAsync(
            invalidBodyResponse,
            "validation_failed",
            "/reason").ConfigureAwait(true);

        using HttpRequestMessage wrongMediaType = JsonCommand(
            "/api/v1/me/api-keys",
            new { name = "valid", group_id = GroupId.Value },
            "wrong-media-type");
        wrongMediaType.Content!.Headers.ContentType =
            new MediaTypeHeaderValue("application/problem+json");
        using HttpResponseMessage wrongMediaTypeResponse = await client.SendAsync(
            wrongMediaType,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongMediaTypeResponse.StatusCode);
        await AssertProblemAsync(
            wrongMediaTypeResponse,
            "unsupported_media_type").ConfigureAwait(true);

        Assert.Equal(0, factory.Port.ListCalls);
        Assert.Equal(0, factory.Port.CreateCalls);
    }

    [Fact]
    public async Task PortFailuresUseStableProblemCodesAndRequiredRetryHeaders()
    {
        await using ApiKeyApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "user");

        factory.Port.CreateResult = Result.Failure<ApiKeyCreatedOutcome>(
            "subscription_required",
            "synthetic");
        using HttpRequestMessage create = JsonCommand(
            "/api/v1/me/api-keys",
            new { name = "valid", group_id = GroupId.Value },
            "subscription-required");
        using HttpResponseMessage forbidden = await client.SendAsync(
            create,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertProblemAsync(forbidden, "subscription_required").ConfigureAwait(true);

        factory.Port.GetResult = Result.Failure<ApiKeyControlPlaneSnapshot>(
            "resource_not_found",
            "synthetic");
        using HttpResponseMessage missing = await client.GetAsync(
            $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertProblemAsync(missing, "resource_not_found").ConfigureAwait(true);

        factory.Port.ListResult = Result.Failure<PoolAI.Modules.Identity.Abstractions.ApiKeyPage>(
            "dependency_unavailable",
            "synthetic",
            retryAfterSeconds: 1);
        using HttpResponseMessage unavailable = await client.GetAsync(
            "/api/v1/me/api-keys",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), unavailable.Headers.RetryAfter?.Delta);
        await AssertProblemAsync(
            unavailable,
            "dependency_unavailable",
            expectedRetryable: true).ConfigureAwait(true);
    }

    [Theory]
    [InlineData("status_code")]
    [InlineData("location")]
    [InlineData("etag")]
    [InlineData("id")]
    [InlineData("user")]
    [InlineData("group")]
    [InlineData("name")]
    [InlineData("prefix")]
    [InlineData("persistent_status")]
    [InlineData("effective_status")]
    [InlineData("expires_at")]
    [InlineData("allowed_cidrs")]
    [InlineData("noncanonical_cidr")]
    [InlineData("last_used_at")]
    [InlineData("version")]
    [InlineData("created_updated")]
    [InlineData("observed_at")]
    [InlineData("malformed_secret")]
    [InlineData("secret_prefix_binding")]
    public async Task CreateOutcomeDriftFailsClosedBeforeWritingSecretOrSuccessHeaders(
        string mismatch)
    {
        await using ApiKeyApiFactory factory = new();
        ApiKeyCreatedOutcome outcome = Created(
            ActorId,
            $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
            "valid");
        factory.Port.CreateResult = Result.Success(mismatch switch
        {
            "status_code" => outcome with
            {
                StatusCode = StatusCodes.Status200OK,
            },
            "location" => outcome with
            {
                Location = $"/api/v1/me/api-keys/{Guid.CreateVersion7():D}",
            },
            "etag" => outcome with { ETag = "\"v2\"" },
            "id" => outcome with
            {
                ApiKey = outcome.ApiKey with { ApiKeyId = default },
            },
            "user" => outcome with
            {
                ApiKey = outcome.ApiKey with { UserId = TargetUserId },
            },
            "group" => outcome with
            {
                ApiKey = outcome.ApiKey with { GroupId = Id(
                    "019bd5e8-30e0-7d4c-a7f2-bb1db0636199") },
            },
            "name" => outcome with
            {
                ApiKey = outcome.ApiKey with { Name = "different" },
            },
            "prefix" => outcome with
            {
                ApiKey = outcome.ApiKey with { Prefix = "invalid prefix" },
            },
            "persistent_status" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    Status = ApiKeyPersistentStatus.Disabled,
                    EffectiveStatus = ApiKeyEffectiveStatus.Disabled,
                },
            },
            "effective_status" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    EffectiveStatus = ApiKeyEffectiveStatus.Expired,
                },
            },
            "expires_at" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    ExpiresAt = Timestamp.AddDays(1),
                },
            },
            "allowed_cidrs" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    AllowedCidrs = ["10.0.0.0/24"],
                },
            },
            "noncanonical_cidr" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    AllowedCidrs = ["10.0.0.1/24"],
                },
            },
            "last_used_at" => outcome with
            {
                ApiKey = outcome.ApiKey with { LastUsedAt = Timestamp },
            },
            "version" => outcome with
            {
                ApiKey = outcome.ApiKey with { Version = 2 },
                ETag = "\"v2\"",
            },
            "created_updated" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    UpdatedAt = Timestamp.AddMinutes(1),
                    ObservedAt = Timestamp.AddMinutes(1),
                },
            },
            "observed_at" => outcome with
            {
                ApiKey = outcome.ApiKey with
                {
                    ObservedAt = Timestamp.AddTicks(-1),
                },
            },
            "malformed_secret" => outcome with
            {
                Secret = "sk-pool-not*base64url",
            },
            "secret_prefix_binding" => outcome with
            {
                Secret = AlternateSecret,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        });
        using HttpClient client = AuthenticatedClient(factory, "user");
        using HttpRequestMessage request = JsonCommand(
            "/api/v1/me/api-keys",
            new { name = "valid", group_id = GroupId.Value },
            $"binding-{mismatch}");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertProblemAsync(response, "internal_error").ConfigureAwait(true);
        string body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(TestSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AlternateSecret, body, StringComparison.Ordinal);
        Assert.Null(response.Headers.ETag);
        Assert.Null(response.Headers.Location);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
        Assert.True(response.Headers.CacheControl.NoCache);
    }

    [Fact]
    public void OnlyTheSixApprovedApiKeyOperationsAreMapped()
    {
        using ApiKeyApiFactory factory = new();
        using HttpClient _ = factory.CreateClient();
        EndpointDataSource dataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        string[] names = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.Contains(
                "api-keys",
                StringComparison.Ordinal) is true)
            .Select(static endpoint => endpoint.Metadata
                .GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Where(static name => name is not null)
            .Select(static name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedOperationNames, names);
    }

    private static HttpRequestMessage JsonCommand(
        string path,
        object body,
        string? idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static HttpClient AuthenticatedClient(ApiKeyApiFactory factory, string role)
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

    private static ApiKeyControlPlaneSnapshot Snapshot(EntityId userId, long version) => new(
        ApiKeyId,
        userId,
        GroupId,
        "automation",
        "sk-pool-AbCdEf12",
        ApiKeyPersistentStatus.Active,
        ApiKeyEffectiveStatus.Active,
        ExpiresAt: null,
        AllowedCidrs: ["10.0.0.0/24"],
        LastUsedAt: null,
        version,
        Timestamp,
        Timestamp.AddMinutes(version),
        Timestamp.AddMinutes(version));

    private static ApiKeyCreatedOutcome Created(
        EntityId userId,
        string location,
        string name,
        DateTimeOffset? expiresAt = null,
        IReadOnlyList<string>? allowedCidrs = null) => new(
        StatusCodes.Status201Created,
        IsReplay: false,
        new ApiKeyControlPlaneSnapshot(
            ApiKeyId,
            userId,
            GroupId,
            name,
            "sk-pool-ABCDEFGH",
            ApiKeyPersistentStatus.Active,
            ApiKeyEffectiveStatus.Active,
            expiresAt,
            allowedCidrs ?? [],
            LastUsedAt: null,
            Version: 1,
            Timestamp,
            Timestamp,
            Timestamp),
        TestSecret,
        "\"v1\"",
        location);

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

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private const string TestSecret =
        "sk-pool-ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopg";
    private const string AlternateSecret =
        "sk-pool-ZBCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopg";

    private sealed class ApiKeyApiFactory : PoolAiApiFactory
    {
        internal FakeApiKeyPort Port { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IApiKeyCreateUseCase>();
                services.RemoveAll<IApiKeyControlPlaneReader>();
                services.AddSingleton<IApiKeyCreateUseCase>(Port);
                services.AddSingleton<IApiKeyControlPlaneReader>(Port);
            });
        }
    }

    private sealed class FakeApiKeyPort : IApiKeyCreateUseCase, IApiKeyControlPlaneReader
    {
        internal Result<PoolAI.Modules.Identity.Abstractions.ApiKeyPage> ListResult { get; set; } =
            Result.Success(new PoolAI.Modules.Identity.Abstractions.ApiKeyPage(
                [],
                null,
                HasMore: false));

        internal Result<ApiKeyControlPlaneSnapshot> GetResult { get; set; } =
            Result.Success(Snapshot(ActorId, version: 1));

        internal Result<ApiKeyCreatedOutcome> CreateResult { get; set; } =
            Result.Success(Created(
                ActorId,
                $"/api/v1/me/api-keys/{ApiKeyId.Value:D}",
                "valid"));

        internal ListApiKeysQuery? LastListQuery { get; private set; }

        internal GetApiKeyQuery? LastGetQuery { get; private set; }

        internal CreateApiKeyCommand? LastCreateCommand { get; private set; }

        internal int ListCalls { get; private set; }

        internal int GetCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        public ValueTask<Result<PoolAI.Modules.Identity.Abstractions.ApiKeyPage>> ListAsync(
            ListApiKeysQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            LastListQuery = query;
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<Result<ApiKeyControlPlaneSnapshot>> GetAsync(
            GetApiKeyQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            LastGetQuery = query;
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
            CreateApiKeyCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreateCommand = command;
            return ValueTask.FromResult(CreateResult);
        }
    }
}

#pragma warning restore MA0051
