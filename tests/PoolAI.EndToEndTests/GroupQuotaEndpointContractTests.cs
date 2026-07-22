using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private static readonly EntityId ActorId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634080"));
    private static readonly EntityId GroupId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634081"));
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
                services.RemoveAll<IGroupActivationOrchestrator>();
                services.AddSingleton<IListGroupsUseCase>(UseCases);
                services.AddSingleton<IGetGroupUseCase>(UseCases);
                services.AddSingleton<ICreateGroupUseCase>(UseCases);
                services.AddSingleton<IUpdateGroupUseCase>(UseCases);
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
        IUpdateGroupUseCase
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

        internal ListGroupsQuery? LastListQuery { get; private set; }

        internal GetGroupQuery? LastGetQuery { get; private set; }

        internal CreateGroupCommand? LastCreateCommand { get; private set; }

        internal UpdateGroupCommand? LastUpdateCommand { get; private set; }

        internal int ListCalls { get; private set; }

        internal int GetCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int UpdateCalls { get; private set; }

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
