#pragma warning disable MA0051 // HTTP contract scenarios keep each complete request/response proof visible.

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
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application;

namespace PoolAI.EndToEndTests;

public sealed class SubscriptionAccessEndpointContractTests
{
    private static readonly EntityId ActorId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0635000");
    private static readonly EntityId UserId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0635001");
    private static readonly EntityId GroupId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0635002");
    private static readonly EntityId TemplateId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0635003");
    private static readonly EntityId SubscriptionId = Id("019bd5e8-30e0-7d4c-a7f2-bb1db0635004");
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-07-17T08:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task TemplateReadEndpointsPreserveLifecyclePaginationAndStrongEntityTag()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.TemplateListResult = Result.Success(new SubscriptionTemplatePage(
            [
                Template(SubscriptionTemplateLifecycle.Active, 7, "Active"),
                Template(SubscriptionTemplateLifecycle.Disabled, 8, "Disabled"),
                Template(SubscriptionTemplateLifecycle.Retired, 9, "Retired"),
            ],
            "next/template/page",
            HasMore: true));
        using HttpClient client = AuthenticatedClient(factory, "operator");

        using HttpResponseMessage list = await client.GetAsync(
            "/api/v1/admin/subscription-templates?cursor=previous%2Fpage&limit=100",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(list).ConfigureAwait(true))
        {
            JsonElement[] data = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(["active", "disabled", "retired"], data.Select(
                static item => item.GetProperty("status").GetString()!).ToArray());
            Assert.Equal("description", data[0].GetProperty("description").GetString());
            Assert.Equal("next/template/page", document.RootElement
                .GetProperty("page").GetProperty("next_cursor").GetString());
            Assert.True(document.RootElement.GetProperty("page").GetProperty("has_more").GetBoolean());
        }

        ListSubscriptionTemplatesQuery listQuery = Assert.IsType<ListSubscriptionTemplatesQuery>(
            factory.UseCases.LastTemplateListQuery);
        Assert.Equal(ActorId, listQuery.Actor.UserId);
        Assert.Equal(SystemRole.Operator, listQuery.Actor.Role);
        Assert.Equal(7, listQuery.Actor.TokenVersion);
        Assert.Equal("previous/page", listQuery.Cursor);
        Assert.Equal(100, listQuery.Limit);

        factory.UseCases.TemplateListResult = Result.Success(
            new SubscriptionTemplatePage([], null, HasMore: false));
        using HttpResponseMessage empty = await client.GetAsync(
            "/api/v1/admin/subscription-templates",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(empty).ConfigureAwait(true))
        {
            JsonElement page = document.RootElement.GetProperty("page");
            Assert.False(page.GetProperty("has_more").GetBoolean());
            Assert.False(page.TryGetProperty("next_cursor", out _));
        }

        factory.UseCases.TemplateGetResult = Result.Success(
            Template(SubscriptionTemplateLifecycle.Active, 12, "Detailed", description: null));
        using HttpClient auditor = AuthenticatedClient(factory, "auditor");
        using HttpResponseMessage get = await auditor.GetAsync(
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v12\"", get.Headers.ETag?.Tag);
        using (JsonDocument document = await ReadJsonAsync(get).ConfigureAwait(true))
        {
            Assert.Equal(TemplateId.Value, document.RootElement.GetProperty("id").GetGuid());
            Assert.False(document.RootElement.TryGetProperty("description", out _));
        }

        GetSubscriptionTemplateQuery getQuery = Assert.IsType<GetSubscriptionTemplateQuery>(
            factory.UseCases.LastTemplateGetQuery);
        Assert.Equal(TemplateId, getQuery.TemplateId);
        Assert.Equal(SystemRole.Auditor, getQuery.Actor.Role);
    }

    [Fact]
    public async Task SelfAndAdminSubscriptionReadsPreserveEffectiveLifecycleAndFilters()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.SubscriptionListResult = Result.Success(new SubscriptionPage(
            [
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Scheduled, 1),
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 2),
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Expired, 3),
                Subscription(SubscriptionLifecycle.Suspended, SubscriptionEffectiveLifecycle.Suspended, 4),
                Subscription(SubscriptionLifecycle.Revoked, SubscriptionEffectiveLifecycle.Revoked, 5),
            ],
            "ignored-for-self",
            HasMore: true));
        using HttpClient user = AuthenticatedClient(factory, "user");

        using HttpResponseMessage self = await user.GetAsync(
            "/api/v1/me/subscriptions",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(self).ConfigureAwait(true))
        {
            JsonElement[] data = document.RootElement.GetProperty("data").EnumerateArray().ToArray();
            Assert.Equal(
                ["scheduled", "active", "expired", "suspended", "revoked"],
                data.Select(static item => item.GetProperty("effective_status").GetString()!).ToArray());
            Assert.Equal(
                ["active", "active", "active", "suspended", "revoked"],
                data.Select(static item => item.GetProperty("status").GetString()!).ToArray());
            Assert.False(document.RootElement.TryGetProperty("page", out _));
        }

        ListSubscriptionsQuery selfQuery = Assert.IsType<ListSubscriptionsQuery>(
            factory.UseCases.LastSubscriptionListQuery);
        Assert.True(selfQuery.IsSelfQuery);
        Assert.Equal(SystemRole.User, selfQuery.Actor.Role);
        Assert.Null(selfQuery.Cursor);
        Assert.Equal(100, selfQuery.Limit);
        Assert.Null(selfQuery.UserId);
        Assert.Null(selfQuery.GroupId);

        factory.UseCases.SubscriptionListResult = Result.Success(new SubscriptionPage(
            [Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 6)],
            "next/subscription/page",
            HasMore: true));
        using HttpClient admin = AuthenticatedClient(factory, "admin");
        using HttpResponseMessage filtered = await admin.GetAsync(
            $"/api/v1/admin/subscriptions?cursor=prior%2Fpage&limit=100&user_id={UserId.Value:D}&group_id={GroupId.Value:D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        using (JsonDocument document = await ReadJsonAsync(filtered).ConfigureAwait(true))
        {
            Assert.Equal("next/subscription/page", document.RootElement
                .GetProperty("page").GetProperty("next_cursor").GetString());
            Assert.True(document.RootElement.GetProperty("page").GetProperty("has_more").GetBoolean());
        }

        ListSubscriptionsQuery adminQuery = Assert.IsType<ListSubscriptionsQuery>(
            factory.UseCases.LastSubscriptionListQuery);
        Assert.False(adminQuery.IsSelfQuery);
        Assert.Equal(SystemRole.Admin, adminQuery.Actor.Role);
        Assert.Equal("prior/page", adminQuery.Cursor);
        Assert.Equal(100, adminQuery.Limit);
        Assert.Equal(UserId, adminQuery.UserId);
        Assert.Equal(GroupId, adminQuery.GroupId);

        factory.UseCases.SubscriptionListResult = Result.Success(
            new SubscriptionPage([], null, HasMore: false));
        using HttpResponseMessage unfiltered = await admin.GetAsync(
            "/api/v1/admin/subscriptions",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, unfiltered.StatusCode);
        adminQuery = Assert.IsType<ListSubscriptionsQuery>(factory.UseCases.LastSubscriptionListQuery);
        Assert.Null(adminQuery.UserId);
        Assert.Null(adminQuery.GroupId);
        Assert.Equal(50, adminQuery.Limit);

        factory.UseCases.SubscriptionGetResult = Result.Success(
            Subscription(SubscriptionLifecycle.Suspended, SubscriptionEffectiveLifecycle.Suspended, 13));
        using HttpClient operatorClient = AuthenticatedClient(factory, "operator");
        using HttpResponseMessage get = await operatorClient.GetAsync(
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v13\"", get.Headers.ETag?.Tag);
        GetSubscriptionQuery getQuery = Assert.IsType<GetSubscriptionQuery>(
            factory.UseCases.LastSubscriptionGetQuery);
        Assert.Equal(SubscriptionId, getQuery.SubscriptionId);
        Assert.Equal(SystemRole.Operator, getQuery.Actor.Role);
    }

    [Fact]
    public async Task TemplateCreateMapsHeadersBodyAndAuditableTransportMetadata()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.TemplateCreateResult = Result.Success(
            new SubscriptionCommandOutcome<SubscriptionTemplateView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                Template(SubscriptionTemplateLifecycle.Active, 3, "Research", description: null),
                "\"v3\"",
                $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}"));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        string userAgent = new('a', 600);
        using HttpRequestMessage request = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscription-templates",
            new
            {
                group_id = GroupId.Value,
                name = "Research",
                description = (string?)null,
                default_duration_days = 3650,
            },
            idempotencyKey: "template-create-success");
        request.Content!.Headers.ContentType!.CharSet = "utf-8";
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v3\"", response.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            response.Headers.Location?.OriginalString);
        string requestId = AssertRequestId(response);
        CreateSubscriptionTemplateCommand command = Assert.IsType<CreateSubscriptionTemplateCommand>(
            factory.UseCases.LastTemplateCreateCommand);
        Assert.Equal(Guid.Parse(requestId), command.RequestId.Value);
        Assert.Equal(ActorId, command.Actor.UserId);
        Assert.Equal("template-create-success", command.IdempotencyKey);
        Assert.Equal(GroupId, command.GroupId);
        Assert.Equal("Research", command.Name);
        Assert.Null(command.Description);
        Assert.Equal(3650, command.DefaultDurationDays);
        Assert.Equal(UserAgentDigest(userAgent), command.UserAgent);
        Assert.Null(command.IpAddress);
    }

    [Fact]
    public async Task TemplateUpdateMapsOptionalFieldsAndBothMutableLifecycles()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.TemplateUpdateResult = Result.Success(
            new SubscriptionCommandOutcome<SubscriptionTemplateView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                Template(SubscriptionTemplateLifecycle.Disabled, 8, "Renamed", description: null),
                "\"v8\""));
        using HttpClient client = AuthenticatedClient(factory, "operator");
        using HttpRequestMessage disable = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            new
            {
                name = "Renamed",
                description = (string?)null,
                default_duration_days = 30,
                status = "disabled",
                reason = "maintenance",
            },
            "application/merge-patch+json",
            "template-disable",
            "\"v7\"");

        using HttpResponseMessage disabledResponse = await client.SendAsync(
            disable,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);
        Assert.Equal("\"v8\"", disabledResponse.Headers.ETag?.Tag);
        UpdateSubscriptionTemplateCommand disabled = Assert.IsType<UpdateSubscriptionTemplateCommand>(
            factory.UseCases.LastTemplateUpdateCommand);
        Assert.Equal(TemplateId, disabled.TemplateId);
        Assert.Equal(7, disabled.ExpectedVersion);
        Assert.True(disabled.NameSpecified);
        Assert.Equal("Renamed", disabled.Name);
        Assert.True(disabled.DescriptionSpecified);
        Assert.Null(disabled.Description);
        Assert.True(disabled.DefaultDurationDaysSpecified);
        Assert.Equal(30, disabled.DefaultDurationDays);
        Assert.True(disabled.StatusSpecified);
        Assert.Equal(SubscriptionTemplateLifecycle.Disabled, disabled.Status);
        Assert.Equal("maintenance", disabled.Reason);

        factory.UseCases.TemplateUpdateResult = Result.Success(
            new SubscriptionCommandOutcome<SubscriptionTemplateView>(
                StatusCodes.Status200OK,
                IsReplay: true,
                Template(SubscriptionTemplateLifecycle.Active, 9, "Renamed"),
                "\"v9\""));
        using HttpRequestMessage activate = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            new { status = "active", reason = "available again" },
            "application/merge-patch+json",
            "template-activate",
            "\"v8\"");
        activate.Headers.TryAddWithoutValidation("User-Agent", "short-agent");
        using HttpResponseMessage activeResponse = await client.SendAsync(
            activate,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        UpdateSubscriptionTemplateCommand active = Assert.IsType<UpdateSubscriptionTemplateCommand>(
            factory.UseCases.LastTemplateUpdateCommand);
        Assert.False(active.NameSpecified);
        Assert.False(active.DescriptionSpecified);
        Assert.False(active.DefaultDurationDaysSpecified);
        Assert.Null(active.DefaultDurationDays);
        Assert.Equal(SubscriptionTemplateLifecycle.Active, active.Status);
        Assert.Equal(UserAgentDigest("short-agent"), active.UserAgent);
    }

    [Fact]
    public async Task TemplateRetireRequiresAuditHeadersAndReturnsNoContentWithCurrentEntityTag()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.TemplateRetireResult = Result.Success(
            new SubscriptionCommandOutcome(StatusCodes.Status204NoContent, IsReplay: false, "\"v11\""));
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "template-retire");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v10\"");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "obsolete template");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("\"v11\"", response.Headers.ETag?.Tag);
        RetireSubscriptionTemplateCommand command = Assert.IsType<RetireSubscriptionTemplateCommand>(
            factory.UseCases.LastTemplateRetireCommand);
        Assert.Equal(TemplateId, command.TemplateId);
        Assert.Equal(10, command.ExpectedVersion);
        Assert.Equal("template-retire", command.IdempotencyKey);
        Assert.Equal("obsolete template", command.Reason);
    }

    [Fact]
    public async Task AssignSubscriptionMapsExplicitAndDefaultedTimeRanges()
    {
        await using SubscriptionApiFactory factory = new();
        factory.UseCases.SubscriptionAssignResult = Result.Success(
            new SubscriptionCommandOutcome<SubscriptionView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Scheduled, 1),
                "\"v1\"",
                $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}"));
        using HttpClient client = AuthenticatedClient(factory, "operator");
        DateTimeOffset startsAt = Timestamp.AddDays(1);
        DateTimeOffset expiresAt = Timestamp.AddDays(31);
        using HttpRequestMessage explicitRange = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            new
            {
                user_id = UserId.Value,
                template_id = TemplateId.Value,
                starts_at = startsAt,
                expires_at = expiresAt,
                reason = "grant access",
            },
            idempotencyKey: "subscription-assign-explicit");

        using HttpResponseMessage explicitResponse = await client.SendAsync(
            explicitRange,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, explicitResponse.StatusCode);
        Assert.Equal("\"v1\"", explicitResponse.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            explicitResponse.Headers.Location?.OriginalString);
        AssignSubscriptionCommand explicitCommand = Assert.IsType<AssignSubscriptionCommand>(
            factory.UseCases.LastSubscriptionAssignCommand);
        Assert.Equal(UserId, explicitCommand.UserId);
        Assert.Equal(TemplateId, explicitCommand.TemplateId);
        Assert.Equal(startsAt, explicitCommand.StartsAt);
        Assert.Equal(expiresAt, explicitCommand.ExpiresAt);
        Assert.Equal("grant access", explicitCommand.Reason);

        using HttpRequestMessage defaults = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            new
            {
                user_id = UserId.Value,
                template_id = TemplateId.Value,
                expires_at = (DateTimeOffset?)null,
                reason = "default duration",
            },
            idempotencyKey: "subscription-assign-defaults");
        using HttpResponseMessage defaultResponse = await client.SendAsync(
            defaults,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, defaultResponse.StatusCode);
        AssignSubscriptionCommand defaultCommand = Assert.IsType<AssignSubscriptionCommand>(
            factory.UseCases.LastSubscriptionAssignCommand);
        Assert.Null(defaultCommand.StartsAt);
        Assert.Null(defaultCommand.ExpiresAt);

        using HttpRequestMessage explicitStartDefaultExpiry = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            new
            {
                user_id = UserId.Value,
                template_id = TemplateId.Value,
                starts_at = startsAt,
                expires_at = (DateTimeOffset?)null,
                reason = "scheduled default duration",
            },
            idempotencyKey: "subscription-assign-start-default-expiry");
        using HttpResponseMessage explicitStartResponse = await client.SendAsync(
            explicitStartDefaultExpiry,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, explicitStartResponse.StatusCode);
        AssignSubscriptionCommand explicitStartCommand = Assert.IsType<AssignSubscriptionCommand>(
            factory.UseCases.LastSubscriptionAssignCommand);
        Assert.Equal(startsAt, explicitStartCommand.StartsAt);
        Assert.Null(explicitStartCommand.ExpiresAt);
    }

    [Fact]
    public async Task UpdateSubscriptionMapsAllPersistentLifecyclesAndOptionalTimes()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        factory.UseCases.SubscriptionUpdateResult = Result.Success(
            new SubscriptionCommandOutcome<SubscriptionView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 8),
                "\"v8\""));
        DateTimeOffset startsAt = Timestamp.AddDays(-1);
        DateTimeOffset expiresAt = Timestamp.AddDays(90);
        using HttpRequestMessage activeRequest = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            new { starts_at = startsAt, expires_at = expiresAt, status = "active", reason = "extend" },
            "application/merge-patch+json",
            "subscription-active",
            "\"v7\"");
        using HttpResponseMessage activeResponse = await client.SendAsync(
            activeRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        Assert.Equal("\"v8\"", activeResponse.Headers.ETag?.Tag);
        UpdateSubscriptionCommand active = Assert.IsType<UpdateSubscriptionCommand>(
            factory.UseCases.LastSubscriptionUpdateCommand);
        Assert.Equal(SubscriptionId, active.SubscriptionId);
        Assert.Equal(7, active.ExpectedVersion);
        Assert.True(active.StartsAtSpecified);
        Assert.Equal(startsAt, active.StartsAt);
        Assert.True(active.ExpiresAtSpecified);
        Assert.Equal(expiresAt, active.ExpiresAt);
        Assert.True(active.StatusSpecified);
        Assert.Equal(SubscriptionLifecycle.Active, active.Status);

        foreach ((string status, SubscriptionLifecycle expected) in new[]
                 {
                     ("suspended", SubscriptionLifecycle.Suspended),
                     ("revoked", SubscriptionLifecycle.Revoked),
                 })
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Patch,
                $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
                new { status, reason = $"set {status}" },
                "application/merge-patch+json",
                $"subscription-{status}",
                "\"v8\"");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            UpdateSubscriptionCommand command = Assert.IsType<UpdateSubscriptionCommand>(
                factory.UseCases.LastSubscriptionUpdateCommand);
            Assert.False(command.StartsAtSpecified);
            Assert.Null(command.StartsAt);
            Assert.False(command.ExpiresAtSpecified);
            Assert.Null(command.ExpiresAt);
            Assert.Equal(expected, command.Status);
        }

        using HttpRequestMessage expiryOnly = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            new { expires_at = expiresAt.AddDays(1), reason = "extend only" },
            "application/merge-patch+json",
            "subscription-expiry-only",
            "\"v8\"");
        using HttpResponseMessage expiryOnlyResponse = await client.SendAsync(
            expiryOnly,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, expiryOnlyResponse.StatusCode);
        UpdateSubscriptionCommand expiryOnlyCommand = Assert.IsType<UpdateSubscriptionCommand>(
            factory.UseCases.LastSubscriptionUpdateCommand);
        Assert.False(expiryOnlyCommand.StatusSpecified);
        Assert.Null(expiryOnlyCommand.Status);
    }

    [Fact]
    public async Task TemplateCreateRejectsTransportValidationAndIdempotencyBoundaries()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscription-templates",
            ValidTemplateCreateBody(),
            "application/problem+json",
            "wrong-content");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);
        await AssertProblemAsync(wrongContentResponse, "unsupported_media_type").ConfigureAwait(true);

        (object Body, string Pointer)[] invalidBodies =
        [
            (new { group_id = Guid.Empty, name = "Valid", default_duration_days = 30 }, "/group_id"),
            (new { group_id = GroupId.Value, name = " ", default_duration_days = 30 }, "/name"),
            (new { group_id = GroupId.Value, name = new string('n', 101), default_duration_days = 30 }, "/name"),
            (new { group_id = GroupId.Value, name = "bad\u0001name", default_duration_days = 30 }, "/name"),
            (new { group_id = GroupId.Value, name = "Valid", description = new string('d', 1001), default_duration_days = 30 }, "/description"),
            (new { group_id = GroupId.Value, name = "Valid", description = "bad\ndescription", default_duration_days = 30 }, "/description"),
            (new { group_id = GroupId.Value, name = "Valid", default_duration_days = 0 }, "/default_duration_days"),
            (new { group_id = GroupId.Value, name = "Valid", default_duration_days = 3651 }, "/default_duration_days"),
        ];
        foreach ((object body, string pointer) in invalidBodies)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/subscription-templates",
                body,
                idempotencyKey: "invalid-template");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer).ConfigureAwait(true);
        }

        using HttpRequestMessage missingKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscription-templates",
            ValidTemplateCreateBody());
        using HttpResponseMessage missingKeyResponse = await client.SendAsync(
            missingKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingKeyResponse.StatusCode);
        await AssertProblemAsync(missingKeyResponse, "idempotency_key_required").ConfigureAwait(true);

        foreach (string invalidKey in new[] { "contains space", new string('k', 129), "bad\nkey" })
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/subscription-templates",
                ValidTemplateCreateBody(),
                idempotencyKey: invalidKey);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request").ConfigureAwait(true);
        }

        Assert.Equal(0, factory.UseCases.TemplateCreateCalls);
    }

    [Fact]
    public async Task TemplateUpdateRejectsFieldAndPreconditionBoundaries()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            new { name = "Valid" },
            "application/json",
            "wrong-content",
            "\"v1\"");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);

        using HttpRequestMessage emptyId = JsonCommand(
            HttpMethod.Patch,
            "/api/v1/admin/subscription-templates/00000000-0000-0000-0000-000000000000",
            new { name = "Valid" },
            "application/merge-patch+json",
            "empty-id",
            "\"v1\"");
        using HttpResponseMessage emptyIdResponse = await client.SendAsync(
            emptyId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptyIdResponse.StatusCode);
        await AssertProblemAsync(emptyIdResponse, "invalid_request").ConfigureAwait(true);

        (object Body, string Pointer)[] invalidBodies =
        [
            (new { }, "/"),
            (new { name = " " }, "/name"),
            (new { name = "bad\u0001name" }, "/name"),
            (new { description = new string('d', 1001) }, "/description"),
            (new { description = "bad\ndescription" }, "/description"),
            (new { default_duration_days = 0 }, "/default_duration_days"),
            (new { default_duration_days = 3651 }, "/default_duration_days"),
            (new { status = "retired", reason = "use delete" }, "/status"),
            (new { status = "disabled" }, "/reason"),
            (new { status = "disabled", reason = " " }, "/reason"),
            (new { name = "Valid", reason = "bad\nreason" }, "/reason"),
        ];
        foreach ((object body, string pointer) in invalidBodies)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Patch,
                $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
                body,
                "application/merge-patch+json",
                "invalid-template-update",
                "\"v1\"");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer).ConfigureAwait(true);
        }

        await AssertTemplateUpdateHeaderFailuresAsync(client).ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.TemplateUpdateCalls);
    }

    [Fact]
    public async Task TemplateRetireRejectsIdentifierHeaderAndReasonBoundaries()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "operator");

        using HttpResponseMessage emptyId = await SendRetireAsync(
            client,
            Guid.Empty,
            "retire-empty-id",
            "\"v1\"",
            "valid reason").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, emptyId.StatusCode);

        using HttpResponseMessage missingKey = await SendRetireAsync(
            client,
            TemplateId.Value,
            null,
            "\"v1\"",
            "valid reason").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingKey.StatusCode);

        using HttpResponseMessage invalidKey = await SendRetireAsync(
            client,
            TemplateId.Value,
            "invalid key",
            "\"v1\"",
            "valid reason").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKey.StatusCode);

        using HttpResponseMessage missingVersion = await SendRetireAsync(
            client,
            TemplateId.Value,
            "retire-missing-version",
            null,
            "valid reason").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingVersion.StatusCode);

        using HttpResponseMessage invalidVersion = await SendRetireAsync(
            client,
            TemplateId.Value,
            "retire-invalid-version",
            "W/\"v1\"",
            "valid reason").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);

        foreach (string? reason in new[] { null, " ", new string('r', 501), "bad\nreason" })
        {
            using HttpResponseMessage response = await SendRetireAsync(
                client,
                TemplateId.Value,
                "retire-invalid-reason",
                "\"v1\"",
                reason).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(
                response,
                "validation_failed",
                "/headers/X-Change-Reason").ConfigureAwait(true);
        }

        Assert.Equal(0, factory.UseCases.TemplateRetireCalls);
    }

    [Fact]
    public async Task SubscriptionListAndGetRejectInvalidParametersBeforeUseCases()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        foreach (string limit in new[] { "0", "101", "not-a-number", "+1", "-1" })
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/api/v1/admin/subscriptions?limit={Uri.EscapeDataString(limit)}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request", "/limit").ConfigureAwait(true);

            using HttpResponseMessage templateResponse = await client.GetAsync(
                $"/api/v1/admin/subscription-templates?limit={Uri.EscapeDataString(limit)}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, templateResponse.StatusCode);
            await AssertProblemAsync(templateResponse, "invalid_request", "/limit")
                .ConfigureAwait(true);
        }

        foreach ((string parameter, string value, string pointer) in new[]
                 {
                     ("user_id", "not-a-uuid", "/user_id"),
                     ("user_id", Guid.Empty.ToString("D"), "/user_id"),
                     ("group_id", "not-a-uuid", "/group_id"),
                     ("group_id", Guid.Empty.ToString("D"), "/group_id"),
                 })
        {
            using HttpResponseMessage response = await client.GetAsync(
                $"/api/v1/admin/subscriptions?{parameter}={value}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request", pointer).ConfigureAwait(true);
        }

        using HttpResponseMessage emptySubscription = await client.GetAsync(
            "/api/v1/admin/subscriptions/00000000-0000-0000-0000-000000000000",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptySubscription.StatusCode);

        using HttpResponseMessage emptyTemplate = await client.GetAsync(
            "/api/v1/admin/subscription-templates/00000000-0000-0000-0000-000000000000",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptyTemplate.StatusCode);

        Assert.Equal(0, factory.UseCases.SubscriptionListCalls);
        Assert.Equal(0, factory.UseCases.TemplateListCalls);
        Assert.Equal(0, factory.UseCases.SubscriptionGetCalls);
        Assert.Equal(0, factory.UseCases.TemplateGetCalls);
    }

    [Fact]
    public async Task AssignSubscriptionRejectsTransportValidationAndIdempotencyBoundaries()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            ValidSubscriptionCreateBody(),
            "application/problem+json",
            "wrong-content");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);

        (object Body, string Pointer)[] invalidBodies =
        [
            (new { user_id = Guid.Empty, template_id = TemplateId.Value, reason = "grant" }, "/user_id"),
            (new { user_id = UserId.Value, template_id = Guid.Empty, reason = "grant" }, "/template_id"),
            (new { user_id = UserId.Value, template_id = TemplateId.Value, reason = " " }, "/reason"),
            (new { user_id = UserId.Value, template_id = TemplateId.Value, reason = "bad\nreason" }, "/reason"),
            (new { user_id = UserId.Value, template_id = TemplateId.Value, reason = new string('r', 501) }, "/reason"),
            (new
            {
                user_id = UserId.Value,
                template_id = TemplateId.Value,
                starts_at = Timestamp.AddDays(2),
                expires_at = Timestamp.AddDays(1),
                reason = "invalid range",
            }, "/expires_at"),
        ];
        foreach ((object body, string pointer) in invalidBodies)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/subscriptions",
                body,
                idempotencyKey: "invalid-assignment");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer).ConfigureAwait(true);
        }

        using HttpRequestMessage missingKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            ValidSubscriptionCreateBody());
        using HttpResponseMessage missingKeyResponse = await client.SendAsync(
            missingKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingKeyResponse.StatusCode);

        using HttpRequestMessage invalidKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            ValidSubscriptionCreateBody(),
            idempotencyKey: "invalid key");
        using HttpResponseMessage invalidKeyResponse = await client.SendAsync(
            invalidKey,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKeyResponse.StatusCode);

        Assert.Equal(0, factory.UseCases.SubscriptionAssignCalls);
    }

    [Fact]
    public async Task UpdateSubscriptionRejectsFieldAndPreconditionBoundaries()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage wrongContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            new { status = "active", reason = "restore" },
            "application/json",
            "wrong-content",
            "\"v1\"");
        using HttpResponseMessage wrongContentResponse = await client.SendAsync(
            wrongContent,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongContentResponse.StatusCode);

        using HttpRequestMessage emptyId = JsonCommand(
            HttpMethod.Patch,
            "/api/v1/admin/subscriptions/00000000-0000-0000-0000-000000000000",
            new { status = "active", reason = "restore" },
            "application/merge-patch+json",
            "empty-id",
            "\"v1\"");
        using HttpResponseMessage emptyIdResponse = await client.SendAsync(
            emptyId,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, emptyIdResponse.StatusCode);

        (object Body, string Pointer)[] invalidBodies =
        [
            (new { reason = "valid reason" }, "/"),
            (new { status = 999, reason = "invalid lifecycle" }, "/status"),
            (new { status = "active", reason = " " }, "/reason"),
            (new { status = "active", reason = "bad\nreason" }, "/reason"),
            (new
            {
                starts_at = Timestamp.AddDays(2),
                expires_at = Timestamp.AddDays(1),
                reason = "invalid range",
            }, "/expires_at"),
        ];
        foreach ((object body, string pointer) in invalidBodies)
        {
            using HttpRequestMessage request = JsonCommand(
                HttpMethod.Patch,
                $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
                body,
                "application/merge-patch+json",
                "invalid-subscription-update",
                "\"v1\"");
            using HttpResponseMessage response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            await AssertProblemAsync(response, "validation_failed", pointer).ConfigureAwait(true);
        }

        await AssertSubscriptionUpdateHeaderFailuresAsync(client).ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.SubscriptionUpdateCalls);
    }

    [Fact]
    public async Task EverySubscriptionEndpointMapsApplicationFailures()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");

        factory.UseCases.SubscriptionListResult = Result.Failure<SubscriptionPage>(
            SubscriptionErrorCodes.ResourceNotFound,
            "synthetic self list failure");
        using HttpResponseMessage self = await client.GetAsync(
            "/api/v1/me/subscriptions",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, self.StatusCode);

        factory.UseCases.TemplateListResult = Result.Failure<SubscriptionTemplatePage>(
            SubscriptionErrorCodes.ResourceNotFound,
            "synthetic template list failure");
        using HttpResponseMessage templateList = await client.GetAsync(
            "/api/v1/admin/subscription-templates",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, templateList.StatusCode);

        factory.UseCases.TemplateGetResult = Result.Failure<SubscriptionTemplateView>(
            SubscriptionErrorCodes.ResourceNotFound,
            "synthetic template get failure");
        using HttpResponseMessage templateGet = await client.GetAsync(
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, templateGet.StatusCode);

        factory.UseCases.TemplateCreateResult = Result.Failure<SubscriptionCommandOutcome<SubscriptionTemplateView>>(
            SubscriptionErrorCodes.ResourceConflict,
            "synthetic template create failure");
        using HttpRequestMessage templateCreateRequest = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscription-templates",
            ValidTemplateCreateBody(),
            idempotencyKey: "template-create-failure");
        using HttpResponseMessage templateCreate = await client.SendAsync(
            templateCreateRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, templateCreate.StatusCode);

        factory.UseCases.TemplateUpdateResult = Result.Failure<SubscriptionCommandOutcome<SubscriptionTemplateView>>(
            SubscriptionErrorCodes.VersionConflict,
            "synthetic template update failure",
            etag: "\"v19\"");
        using HttpRequestMessage templateUpdateRequest = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            new { name = "Stale" },
            "application/merge-patch+json",
            "template-update-failure",
            "\"v7\"");
        using HttpResponseMessage templateUpdate = await client.SendAsync(
            templateUpdateRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.PreconditionFailed, templateUpdate.StatusCode);
        Assert.Equal("\"v19\"", templateUpdate.Headers.ETag?.Tag);

        factory.UseCases.TemplateRetireResult = Result.Failure<SubscriptionCommandOutcome>(
            SubscriptionErrorCodes.ResourceConflict,
            "synthetic template retire failure");
        using HttpResponseMessage templateRetire = await SendRetireAsync(
            client,
            TemplateId.Value,
            "template-retire-failure",
            "\"v7\"",
            "retire").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, templateRetire.StatusCode);

        factory.UseCases.SubscriptionListResult = Result.Failure<SubscriptionPage>(
            SubscriptionErrorCodes.ResourceNotFound,
            "synthetic admin list failure");
        using HttpResponseMessage subscriptionList = await client.GetAsync(
            "/api/v1/admin/subscriptions",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, subscriptionList.StatusCode);

        factory.UseCases.SubscriptionGetResult = Result.Failure<SubscriptionView>(
            SubscriptionErrorCodes.ResourceNotFound,
            "synthetic subscription get failure");
        using HttpResponseMessage subscriptionGet = await client.GetAsync(
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, subscriptionGet.StatusCode);

        factory.UseCases.SubscriptionAssignResult = Result.Failure<SubscriptionCommandOutcome<SubscriptionView>>(
            SubscriptionErrorCodes.SubscriptionConflict,
            "synthetic assignment failure");
        using HttpRequestMessage assignRequest = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            ValidSubscriptionCreateBody(),
            idempotencyKey: "subscription-assign-failure");
        using HttpResponseMessage assign = await client.SendAsync(
            assignRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, assign.StatusCode);

        factory.UseCases.SubscriptionUpdateResult = Result.Failure<SubscriptionCommandOutcome<SubscriptionView>>(
            SubscriptionErrorCodes.ResourceConflict,
            "synthetic subscription update failure");
        using HttpRequestMessage updateRequest = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            new { status = "suspended", reason = "suspend" },
            "application/merge-patch+json",
            "subscription-update-failure",
            "\"v7\"");
        using HttpResponseMessage update = await client.SendAsync(
            updateRequest,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, update.StatusCode);
    }

    [Fact]
    public async Task FrozenApplicationErrorsMapToCanonicalHttpPresentation()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin");
        (string Code, HttpStatusCode Status, bool Retryable, bool HasRetryAfter, string? ETag)[] cases =
        [
            (SubscriptionErrorCodes.RoleRequired, HttpStatusCode.Forbidden, false, false, null),
            (SubscriptionErrorCodes.GroupDisabled, HttpStatusCode.Forbidden, false, false, null),
            (SubscriptionErrorCodes.ResourceNotFound, HttpStatusCode.NotFound, false, false, null),
            (SubscriptionErrorCodes.IdempotencyConflict, HttpStatusCode.Conflict, false, false, null),
            (SubscriptionErrorCodes.ResourceConflict, HttpStatusCode.Conflict, false, false, null),
            (SubscriptionErrorCodes.SubscriptionConflict, HttpStatusCode.Conflict, false, false, null),
            (SubscriptionErrorCodes.SubscriptionTemplateDisabled, HttpStatusCode.Conflict, false, false, null),
            (SubscriptionErrorCodes.VersionConflict, HttpStatusCode.PreconditionFailed, true, false, "\"v23\""),
            (SubscriptionErrorCodes.ValidationFailed, HttpStatusCode.UnprocessableEntity, false, false, null),
            (SubscriptionErrorCodes.InvalidRequest, HttpStatusCode.BadRequest, false, false, null),
            (SubscriptionErrorCodes.CoordinationUnavailable, HttpStatusCode.ServiceUnavailable, true, true, null),
            ("unknown_failure", HttpStatusCode.InternalServerError, false, false, null),
        ];

        foreach ((string code, HttpStatusCode status, bool retryable, bool hasRetryAfter, string? etag) in cases)
        {
            factory.UseCases.TemplateListResult = Result.Failure<SubscriptionTemplatePage>(
                code,
                "synthetic failure",
                etag: etag);
            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/admin/subscription-templates",
                TestContext.Current.CancellationToken);
            Assert.Equal(status, response.StatusCode);
            Assert.Equal(etag, response.Headers.ETag?.Tag);
            Assert.Equal(
                hasRetryAfter ? TimeSpan.FromSeconds(1) : null,
                response.Headers.RetryAfter?.Delta);
            await AssertProblemAsync(
                response,
                code,
                string.Equals(code, SubscriptionErrorCodes.ValidationFailed, StringComparison.Ordinal)
                    ? "/"
                    : null,
                retryable).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ApplicationPresentationCarriesCustomDetailFieldErrorsAndRetryMetadata()
    {
        await using SubscriptionApiFactory factory = new();
        IReadOnlyDictionary<string, IReadOnlyList<string>> errors =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["/cursor"] = ["The cursor is no longer valid."],
            };
        factory.UseCases.TemplateListResult = Result.Failure<SubscriptionTemplatePage>(
            SubscriptionErrorCodes.CoordinationUnavailable,
            "synthetic cursor failure",
            retryAfterSeconds: 2,
            presentation: new ResultErrorPresentation(
                SubscriptionErrorCodes.CoordinationUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                "Cursor service unavailable",
                "The supplied cursor cannot be resumed yet.",
                Retryable: true,
                RetryAfterSeconds: 2,
                Errors: errors));
        using HttpClient client = AuthenticatedClient(factory, "admin");

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/subscription-templates?cursor=stale",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), response.Headers.RetryAfter?.Delta);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true);
        Assert.Equal("Cursor service unavailable", document.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, document.RootElement.GetProperty("retry_after_seconds").GetInt64());
        Assert.Equal(
            "The cursor is no longer valid.",
            document.RootElement.GetProperty("errors").GetProperty("/cursor")[0].GetString());
    }

    [Fact]
    public async Task RuntimePoliciesRejectUserWritesAndMissingAuthentication()
    {
        await using SubscriptionApiFactory factory = new();
        using HttpClient user = AuthenticatedClient(factory, "user");
        using HttpRequestMessage write = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            ValidSubscriptionCreateBody(),
            idempotencyKey: "forbidden-write");
        using HttpResponseMessage forbidden = await user.SendAsync(
            write,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AssertProblemAsync(forbidden, "role_required").ConfigureAwait(true);

        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthorized = await anonymous.GetAsync(
            "/api/v1/admin/subscription-templates",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains(
            unauthorized.Headers.WwwAuthenticate,
            static header => string.Equals(header.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(unauthorized, "authentication_required").ConfigureAwait(true);
        Assert.Equal(0, factory.UseCases.SubscriptionAssignCalls);
    }

    [Fact]
    public async Task EndpointFilterRejectsAuthenticatedPrincipalWithInvalidClaims()
    {
        await using SubscriptionEndpointFilterApiFactory factory = new();
        (Guid SubjectId, long? TokenVersion, IReadOnlyList<string> Roles)[] cases =
        [
            (ActorId.Value, 7, ["owner", "admin"]),
            (Guid.Empty, 7, ["admin"]),
            (ActorId.Value, null, ["admin"]),
            (ActorId.Value, 0, ["admin"]),
        ];
        foreach ((Guid subjectId, long? tokenVersion, IReadOnlyList<string> roles) in cases)
        {
            using HttpClient client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                IdentityAuthorizationTests.CreateJwt(
                    factory.JwtSigningKey,
                    "PoolAI",
                    "PoolAI.Web",
                    role: null,
                    tokenVersion,
                    TimeProvider.System.GetUtcNow().AddMinutes(5),
                    subjectId,
                    roleClaims: roles));

            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/admin/subscription-templates",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(
                response.Headers.WwwAuthenticate,
                static header => string.Equals(header.Scheme, "Bearer", StringComparison.Ordinal));
            await AssertProblemAsync(response, "invalid_user_token").ConfigureAwait(true);
        }

        Assert.Equal(0, factory.UseCases.TemplateListCalls);
    }

    private static async ValueTask AssertTemplateUpdateHeaderFailuresAsync(HttpClient client)
    {
        await AssertUpdateHeaderFailuresAsync(
            client,
            $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}",
            new { name = "Valid" }).ConfigureAwait(false);
    }

    private static async ValueTask AssertSubscriptionUpdateHeaderFailuresAsync(HttpClient client)
    {
        await AssertUpdateHeaderFailuresAsync(
            client,
            $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}",
            new { status = "active", reason = "restore" }).ConfigureAwait(false);
    }

    private static async ValueTask AssertUpdateHeaderFailuresAsync(
        HttpClient client,
        string path,
        object body)
    {
        using HttpRequestMessage missingKey = JsonCommand(
            HttpMethod.Patch,
            path,
            body,
            "application/merge-patch+json",
            ifMatch: "\"v1\"");
        using HttpResponseMessage missingKeyResponse = await client.SendAsync(
            missingKey,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingKeyResponse.StatusCode);
        await AssertProblemAsync(missingKeyResponse, "idempotency_key_required").ConfigureAwait(false);

        using HttpRequestMessage invalidKey = JsonCommand(
            HttpMethod.Patch,
            path,
            body,
            "application/merge-patch+json",
            "invalid key",
            "\"v1\"");
        using HttpResponseMessage invalidKeyResponse = await client.SendAsync(
            invalidKey,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.BadRequest, invalidKeyResponse.StatusCode);

        using HttpRequestMessage missingVersion = JsonCommand(
            HttpMethod.Patch,
            path,
            body,
            "application/merge-patch+json",
            "missing-version");
        using HttpResponseMessage missingVersionResponse = await client.SendAsync(
            missingVersion,
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missingVersionResponse.StatusCode);
        await AssertProblemAsync(missingVersionResponse, "if_match_required").ConfigureAwait(false);

        foreach (string etag in new[]
                 {
                     "v1", "W/\"v1\"", "\"v0\"", "\"v01\"", "\"v\"", "*", "\"v1\", \"v2\"",
                 })
        {
            using HttpRequestMessage invalid = JsonCommand(
                HttpMethod.Patch,
                path,
                body,
                "application/merge-patch+json",
                "invalid-version",
                etag);
            using HttpResponseMessage response = await client.SendAsync(
                invalid,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertProblemAsync(response, "invalid_request").ConfigureAwait(false);
        }
    }

    private static async ValueTask<HttpResponseMessage> SendRetireAsync(
        HttpClient client,
        Guid templateId,
        string? idempotencyKey,
        string? ifMatch,
        string? reason)
    {
        HttpRequestMessage request = new(
            HttpMethod.Delete,
            $"/api/v1/admin/subscription-templates/{templateId:D}");
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (reason is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Change-Reason", reason);
        }

        try
        {
            return await client.SendAsync(
                request,
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
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

    private static HttpClient AuthenticatedClient(SubscriptionApiFactory factory, string role)
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
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out IEnumerable<string>? values));
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

    private static object ValidTemplateCreateBody() => new
    {
        group_id = GroupId.Value,
        name = "Valid",
        default_duration_days = 30,
    };

    private static object ValidSubscriptionCreateBody() => new
    {
        user_id = UserId.Value,
        template_id = TemplateId.Value,
        reason = "grant access",
    };

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

    private static SubscriptionTemplateView Template(
        SubscriptionTemplateLifecycle lifecycle,
        long version,
        string name,
        string? description = "description") => new(
        TemplateId,
        GroupId,
        name,
        description,
        30,
        lifecycle,
        version,
        Timestamp,
        Timestamp.AddMinutes(version));

    private static SubscriptionView Subscription(
        SubscriptionLifecycle lifecycle,
        SubscriptionEffectiveLifecycle effectiveLifecycle,
        long version) => new(
        SubscriptionId,
        UserId,
        GroupId,
        TemplateId,
        "Research",
        Timestamp,
        Timestamp.AddDays(30),
        lifecycle,
        effectiveLifecycle,
        ActorId,
        version,
        Timestamp,
        Timestamp.AddMinutes(version),
        Timestamp.AddMinutes(version));

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private class SubscriptionApiFactory : PoolAiApiFactory
    {
        internal FakeSubscriptionUseCases UseCases { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IListSubscriptionTemplatesUseCase>();
                services.RemoveAll<IGetSubscriptionTemplateUseCase>();
                services.RemoveAll<ICreateSubscriptionTemplateUseCase>();
                services.RemoveAll<IUpdateSubscriptionTemplateUseCase>();
                services.RemoveAll<IRetireSubscriptionTemplateUseCase>();
                services.RemoveAll<IListSubscriptionsUseCase>();
                services.RemoveAll<IGetSubscriptionUseCase>();
                services.RemoveAll<IAssignSubscriptionUseCase>();
                services.RemoveAll<IUpdateSubscriptionUseCase>();
                services.AddSingleton<IListSubscriptionTemplatesUseCase>(UseCases);
                services.AddSingleton<IGetSubscriptionTemplateUseCase>(UseCases);
                services.AddSingleton<ICreateSubscriptionTemplateUseCase>(UseCases);
                services.AddSingleton<IUpdateSubscriptionTemplateUseCase>(UseCases);
                services.AddSingleton<IRetireSubscriptionTemplateUseCase>(UseCases);
                services.AddSingleton<IListSubscriptionsUseCase>(UseCases);
                services.AddSingleton<IGetSubscriptionUseCase>(UseCases);
                services.AddSingleton<IAssignSubscriptionUseCase>(UseCases);
                services.AddSingleton<IUpdateSubscriptionUseCase>(UseCases);
            });
        }
    }

    private sealed class SubscriptionEndpointFilterApiFactory : SubscriptionApiFactory
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

    private sealed class FakeSubscriptionUseCases :
        IListSubscriptionTemplatesUseCase,
        IGetSubscriptionTemplateUseCase,
        ICreateSubscriptionTemplateUseCase,
        IUpdateSubscriptionTemplateUseCase,
        IRetireSubscriptionTemplateUseCase,
        IListSubscriptionsUseCase,
        IGetSubscriptionUseCase,
        IAssignSubscriptionUseCase,
        IUpdateSubscriptionUseCase
    {
        internal Result<SubscriptionTemplatePage> TemplateListResult { get; set; } = Result.Success(
            new SubscriptionTemplatePage([], null, false));

        internal Result<SubscriptionTemplateView> TemplateGetResult { get; set; } = Result.Success(
            Template(SubscriptionTemplateLifecycle.Active, 1, "Default"));

        internal Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> TemplateCreateResult { get; set; } =
            Result.Success(new SubscriptionCommandOutcome<SubscriptionTemplateView>(
                StatusCodes.Status201Created,
                false,
                Template(SubscriptionTemplateLifecycle.Active, 1, "Default"),
                "\"v1\"",
                $"/api/v1/admin/subscription-templates/{TemplateId.Value:D}"));

        internal Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> TemplateUpdateResult { get; set; } =
            Result.Success(new SubscriptionCommandOutcome<SubscriptionTemplateView>(
                StatusCodes.Status200OK,
                false,
                Template(SubscriptionTemplateLifecycle.Active, 2, "Default"),
                "\"v2\""));

        internal Result<SubscriptionCommandOutcome> TemplateRetireResult { get; set; } = Result.Success(
            new SubscriptionCommandOutcome(StatusCodes.Status204NoContent, false, "\"v2\""));

        internal Result<SubscriptionPage> SubscriptionListResult { get; set; } = Result.Success(
            new SubscriptionPage([], null, false));

        internal Result<SubscriptionView> SubscriptionGetResult { get; set; } = Result.Success(
            Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 1));

        internal Result<SubscriptionCommandOutcome<SubscriptionView>> SubscriptionAssignResult { get; set; } =
            Result.Success(new SubscriptionCommandOutcome<SubscriptionView>(
                StatusCodes.Status201Created,
                false,
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 1),
                "\"v1\"",
                $"/api/v1/admin/subscriptions/{SubscriptionId.Value:D}"));

        internal Result<SubscriptionCommandOutcome<SubscriptionView>> SubscriptionUpdateResult { get; set; } =
            Result.Success(new SubscriptionCommandOutcome<SubscriptionView>(
                StatusCodes.Status200OK,
                false,
                Subscription(SubscriptionLifecycle.Active, SubscriptionEffectiveLifecycle.Active, 2),
                "\"v2\""));

        internal ListSubscriptionTemplatesQuery? LastTemplateListQuery { get; private set; }

        internal GetSubscriptionTemplateQuery? LastTemplateGetQuery { get; private set; }

        internal CreateSubscriptionTemplateCommand? LastTemplateCreateCommand { get; private set; }

        internal UpdateSubscriptionTemplateCommand? LastTemplateUpdateCommand { get; private set; }

        internal RetireSubscriptionTemplateCommand? LastTemplateRetireCommand { get; private set; }

        internal ListSubscriptionsQuery? LastSubscriptionListQuery { get; private set; }

        internal GetSubscriptionQuery? LastSubscriptionGetQuery { get; private set; }

        internal AssignSubscriptionCommand? LastSubscriptionAssignCommand { get; private set; }

        internal UpdateSubscriptionCommand? LastSubscriptionUpdateCommand { get; private set; }

        internal int TemplateListCalls { get; private set; }

        internal int TemplateGetCalls { get; private set; }

        internal int TemplateCreateCalls { get; private set; }

        internal int TemplateUpdateCalls { get; private set; }

        internal int TemplateRetireCalls { get; private set; }

        internal int SubscriptionListCalls { get; private set; }

        internal int SubscriptionGetCalls { get; private set; }

        internal int SubscriptionAssignCalls { get; private set; }

        internal int SubscriptionUpdateCalls { get; private set; }

        public ValueTask<Result<SubscriptionTemplatePage>> ExecuteAsync(
            ListSubscriptionTemplatesQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateListCalls++;
            LastTemplateListQuery = query;
            return ValueTask.FromResult(TemplateListResult);
        }

        public ValueTask<Result<SubscriptionTemplateView>> ExecuteAsync(
            GetSubscriptionTemplateQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateGetCalls++;
            LastTemplateGetQuery = query;
            return ValueTask.FromResult(TemplateGetResult);
        }

        public ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
            CreateSubscriptionTemplateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateCreateCalls++;
            LastTemplateCreateCommand = command;
            return ValueTask.FromResult(TemplateCreateResult);
        }

        public ValueTask<Result<SubscriptionCommandOutcome<SubscriptionTemplateView>>> ExecuteAsync(
            UpdateSubscriptionTemplateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateUpdateCalls++;
            LastTemplateUpdateCommand = command;
            return ValueTask.FromResult(TemplateUpdateResult);
        }

        public ValueTask<Result<SubscriptionCommandOutcome>> ExecuteAsync(
            RetireSubscriptionTemplateCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateRetireCalls++;
            LastTemplateRetireCommand = command;
            return ValueTask.FromResult(TemplateRetireResult);
        }

        public ValueTask<Result<SubscriptionPage>> ExecuteAsync(
            ListSubscriptionsQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionListCalls++;
            LastSubscriptionListQuery = query;
            return ValueTask.FromResult(SubscriptionListResult);
        }

        public ValueTask<Result<SubscriptionView>> ExecuteAsync(
            GetSubscriptionQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionGetCalls++;
            LastSubscriptionGetQuery = query;
            return ValueTask.FromResult(SubscriptionGetResult);
        }

        public ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
            AssignSubscriptionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionAssignCalls++;
            LastSubscriptionAssignCommand = command;
            return ValueTask.FromResult(SubscriptionAssignResult);
        }

        public ValueTask<Result<SubscriptionCommandOutcome<SubscriptionView>>> ExecuteAsync(
            UpdateSubscriptionCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionUpdateCalls++;
            LastSubscriptionUpdateCommand = command;
            return ValueTask.FromResult(SubscriptionUpdateResult);
        }
    }
}

#pragma warning restore MA0051
