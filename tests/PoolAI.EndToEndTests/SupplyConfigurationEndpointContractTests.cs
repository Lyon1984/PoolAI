#pragma warning disable MA0051 // HTTP contract scenarios keep the complete protocol visible.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;

namespace PoolAI.EndToEndTests;

public sealed class SupplyConfigurationEndpointContractTests
{
    private static readonly EntityId ActorId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634280"));
    private static readonly EntityId GroupId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634281"));
    private static readonly EntityId ChannelId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634282"));
    private static readonly EntityId AccountId = new(Guid.Parse(
        "019bd5e8-30e0-7d4c-a7f2-bb1db0634283"));
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
        "2026-07-30T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task ChannelOperationRoutesSerializeAndForwardFrozenContract()
    {
        await using SupplyApiFactory factory = new();
        using HttpClient operatorClient = AuthenticatedClient(factory, "operator");

        using HttpResponseMessage list = await operatorClient.GetAsync(
            "/api/v1/admin/channels?cursor=previous-channel&limit=25",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("application/json", list.Content.Headers.ContentType?.MediaType);
        using (JsonDocument document = await ReadJsonAsync(list).ConfigureAwait(true))
        {
            JsonElement item = Assert.Single(
                document.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(ChannelId.Value, item.GetProperty("id").GetGuid());
            Assert.Equal("openai", item.GetProperty("platform").GetString());
            Assert.Equal(
                "openai_compatible",
                item.GetProperty("provider").GetString());
            Assert.Equal("disabled", item.GetProperty("status").GetString());
            Assert.Equal(
                "next-channel",
                document.RootElement.GetProperty("page")
                    .GetProperty("next_cursor").GetString());
        }

        ListChannelsQuery listQuery = Assert.IsType<ListChannelsQuery>(
            factory.UseCases.LastListChannel);
        Assert.Equal(AccountControlRole.Operator, listQuery.Actor.Role);
        Assert.Equal(ActorId, listQuery.Actor.UserId);
        Assert.Equal("previous-channel", listQuery.Cursor);
        Assert.Equal(25, listQuery.Limit);

        factory.UseCases.GetChannelResult = Result.Success(
            ChannelViewOf(ChannelLifecycle.Active, version: 2));
        using HttpClient auditorClient = AuthenticatedClient(factory, "auditor");
        using HttpResponseMessage get = await auditorClient.GetAsync(
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v2\"", get.Headers.ETag?.Tag);
        GetChannelQuery getQuery = Assert.IsType<GetChannelQuery>(
            factory.UseCases.LastGetChannel);
        Assert.Equal(AccountControlRole.Auditor, getQuery.Actor.Role);
        Assert.Equal(ChannelId, getQuery.ChannelId);

        factory.UseCases.CreateChannelResult = Result.Success(
            new SupplyCommandOutcome<ChannelView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                ChannelViewOf(ChannelLifecycle.Disabled, version: 3),
                "\"v3\""));
        using HttpClient operatorCreateClient = AuthenticatedClient(
            factory,
            "operator");
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/channels",
            ValidChannelCreate(),
            idempotencyKey: "channel-create");
        using HttpResponseMessage created = await operatorCreateClient.SendAsync(
            create,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        Assert.Equal("\"v3\"", created.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            created.Headers.Location?.OriginalString);
        CreateChannelCommand createCommand = Assert.IsType<CreateChannelCommand>(
            factory.UseCases.LastCreateChannel);
        Assert.Equal("channel-create", createCommand.IdempotencyKey);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, createCommand.Provider);
        Assert.True(createCommand.Capabilities.Responses);
        Assert.Equal("gpt-5.1", Assert.Single(createCommand.ModelMappings).ClientModel);

        factory.UseCases.UpdateChannelResult = Result.Success(
            new SupplyCommandOutcome<ChannelView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                ChannelViewOf(ChannelLifecycle.Active, version: 4),
                "\"v4\""));
        using HttpClient adminClient = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage patch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            new
            {
                name = "Primary active",
                status = "active",
                reason = "validated model mapping",
            },
            "application/merge-patch+json",
            "channel-activate",
            "\"v3\"");
        using HttpResponseMessage patched = await adminClient.SendAsync(
            patch,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("\"v4\"", patched.Headers.ETag?.Tag);
        UpdateChannelCommand patchCommand = Assert.IsType<UpdateChannelCommand>(
            factory.UseCases.LastUpdateChannel);
        Assert.Equal(ChannelId, patchCommand.ChannelId);
        Assert.Equal(3, patchCommand.ExpectedVersion);
        Assert.True(patchCommand.NameSpecified);
        Assert.Equal("Primary active", patchCommand.Name);
        Assert.True(patchCommand.StatusSpecified);
        Assert.Equal(ChannelLifecycle.Active, patchCommand.Status);
        Assert.Equal("validated model mapping", patchCommand.Reason);

        factory.UseCases.RetireChannelResult = Result.Success(
            new SupplyCommandOutcome(
                StatusCodes.Status204NoContent,
                IsReplay: false,
                "\"v5\""));
        using HttpRequestMessage retire = new(
            HttpMethod.Delete,
            $"/api/v1/admin/channels/{ChannelId.Value:D}");
        retire.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            "channel-retire");
        retire.Headers.TryAddWithoutValidation("If-Match", "\"v4\"");
        retire.Headers.TryAddWithoutValidation(
            "X-Change-Reason",
            "upstream contract removed");
        using HttpClient operatorRetireClient = AuthenticatedClient(
            factory,
            "operator");
        using HttpResponseMessage retired = await operatorRetireClient.SendAsync(
            retire,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, retired.StatusCode);
        Assert.Equal("\"v5\"", retired.Headers.ETag?.Tag);
        RetireChannelCommand retireCommand = Assert.IsType<RetireChannelCommand>(
            factory.UseCases.LastRetireChannel);
        Assert.Equal(4, retireCommand.ExpectedVersion);
        Assert.Equal("channel-retire", retireCommand.IdempotencyKey);
        Assert.Equal("upstream contract removed", retireCommand.Reason);
    }

    [Fact]
    public async Task ChannelAndConfigurationRbacRespectFrozenReadAndWriteRoles()
    {
        await using SupplyApiFactory factory = new();
        foreach (string role in new[] { "admin", "operator", "auditor" })
        {
            using HttpClient reader = AuthenticatedClient(factory, role);
            using HttpResponseMessage channel = await reader.GetAsync(
                $"/api/v1/admin/channels/{ChannelId.Value:D}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, channel.StatusCode);

            using HttpResponseMessage configuration = await reader.GetAsync(
                $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, configuration.StatusCode);
        }

        foreach (string role in new[] { "admin", "operator" })
        {
            using HttpClient writer = AuthenticatedClient(factory, role);
            using HttpRequestMessage createChannel = JsonCommand(
                HttpMethod.Post,
                "/api/v1/admin/channels",
                ValidChannelCreate(),
                idempotencyKey: $"channel-write-{role}");
            using HttpResponseMessage channelWrite = await writer.SendAsync(
                createChannel,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, channelWrite.StatusCode);
        }

        using HttpClient auditor = AuthenticatedClient(factory, "auditor");
        using HttpRequestMessage auditorChannelWrite = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/channels",
            ValidChannelCreate(),
            idempotencyKey: "channel-write-auditor");
        using HttpResponseMessage forbiddenChannelWrite = await auditor.SendAsync(
            auditorChannelWrite,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            forbiddenChannelWrite,
            HttpStatusCode.Forbidden,
            "role_required").ConfigureAwait(true);

        foreach (string role in new[] { "operator", "auditor" })
        {
            using HttpClient writer = AuthenticatedClient(factory, role);
            using HttpRequestMessage createConfiguration = JsonCommand(
                HttpMethod.Post,
                $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
                new
                {
                    channel_id = (Guid?)null,
                    account_bindings = Array.Empty<object>(),
                },
                idempotencyKey: $"supply-write-{role}");
            using HttpResponseMessage forbidden = await writer.SendAsync(
                createConfiguration,
                TestContext.Current.CancellationToken);
            await AssertProblemAsync(
                forbidden,
                HttpStatusCode.Forbidden,
                "role_required").ConfigureAwait(true);
        }

        using HttpClient user = AuthenticatedClient(factory, "user");
        using HttpResponseMessage forbiddenChannelRead = await user.GetAsync(
            "/api/v1/admin/channels",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            forbiddenChannelRead,
            HttpStatusCode.Forbidden,
            "role_required").ConfigureAwait(true);
        using HttpResponseMessage forbiddenConfigurationRead = await user.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            forbiddenConfigurationRead,
            HttpStatusCode.Forbidden,
            "role_required").ConfigureAwait(true);

        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage unauthorized = await anonymous.GetAsync(
            "/api/v1/admin/channels",
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            unauthorized,
            HttpStatusCode.Unauthorized,
            "authentication_required").ConfigureAwait(true);
    }

    [Fact]
    public async Task SupplyConfigurationLifecycleAndActivationRacesRespectOwnership()
    {
        await using SupplyApiFactory factory = new();
        factory.UseCases.GetConfigurationResult = Result.Success(
            ConfigurationView(
                version: 1,
                channelId: null,
                bindings: []));
        using HttpClient auditor = AuthenticatedClient(factory, "auditor");

        using HttpResponseMessage get = await auditor.GetAsync(
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v1\"", get.Headers.ETag?.Tag);
        using (JsonDocument document = await ReadJsonAsync(get).ConfigureAwait(true))
        {
            Assert.Equal(
                GroupId.Value,
                document.RootElement.GetProperty("group_id").GetGuid());
            Assert.Equal(
                JsonValueKind.Null,
                document.RootElement.GetProperty("channel_id").ValueKind);
            Assert.Empty(
                document.RootElement.GetProperty("account_bindings")
                    .EnumerateArray());
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt64());
        }

        GetGroupSupplyConfigurationQuery getQuery =
            Assert.IsType<GetGroupSupplyConfigurationQuery>(
                factory.UseCases.LastGetConfiguration);
        Assert.Equal(AccountControlRole.Auditor, getQuery.Actor.Role);
        Assert.Equal(GroupId, getQuery.GroupId);

        factory.UseCases.CreateConfigurationResult = Result.Success(
            new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                ConfigurationView(
                    version: 1,
                    channelId: null,
                    bindings: []),
                "\"v1\""));
        using HttpClient admin = AuthenticatedClient(factory, "admin");
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = (Guid?)null,
                account_bindings = Array.Empty<object>(),
            },
            idempotencyKey: "supply-create-empty");
        using HttpResponseMessage created = await admin.SendAsync(
            create,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/json", created.Content.Headers.ContentType?.MediaType);
        Assert.Equal("\"v1\"", created.Headers.ETag?.Tag);
        Assert.Equal(
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            created.Headers.Location?.OriginalString);
        CreateGroupSupplyConfigurationCommand createCommand =
            Assert.IsType<CreateGroupSupplyConfigurationCommand>(
                factory.UseCases.LastCreateConfiguration);
        Assert.Equal(GroupId, createCommand.GroupId);
        Assert.Equal(AccountControlRole.Admin, createCommand.Actor.Role);
        Assert.Null(createCommand.ChannelId);
        Assert.Empty(createCommand.AccountBindings);

        GroupSupplyBindingView binding = new(
            AccountId,
            Enabled: true,
            PriorityOverride: -10,
            WeightOverride: 80);
        factory.UseCases.PatchConfigurationResult = Result.Success(
            new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                ConfigurationView(
                    version: 2,
                    channelId: ChannelId,
                    bindings: [binding]),
                "\"v2\""));
        using HttpRequestMessage patch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = ChannelId.Value,
                account_bindings = new[]
                {
                    new
                    {
                        account_id = AccountId.Value,
                        enabled = true,
                        priority_override = -10,
                        weight_override = 80,
                    },
                },
                reason = "supply readiness candidate",
            },
            "application/merge-patch+json",
            "supply-activate-candidate",
            "\"v1\"");
        using HttpResponseMessage patched = await admin.SendAsync(
            patch,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("\"v2\"", patched.Headers.ETag?.Tag);
        PatchGroupSupplyConfigurationCommand patchCommand =
            Assert.IsType<PatchGroupSupplyConfigurationCommand>(
                factory.UseCases.LastPatchConfiguration);
        Assert.Equal(GroupId, patchCommand.GroupId);
        Assert.Equal(1, patchCommand.ExpectedVersion);
        Assert.True(patchCommand.ChannelSpecified);
        Assert.Equal(ChannelId, patchCommand.ChannelId);
        Assert.True(patchCommand.AccountBindingsSpecified);
        GroupSupplyBindingView mapped = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<GroupSupplyBindingView>>(
                patchCommand.AccountBindings));
        Assert.Equal(AccountId, mapped.AccountId);
        Assert.True(mapped.Enabled);
        Assert.Equal(-10, mapped.PriorityOverride);
        Assert.Equal(80, mapped.WeightOverride);
        Assert.Equal("supply readiness candidate", patchCommand.Reason);

        factory.UseCases.PatchConfigurationResult =
            Result.Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                SupplyControlErrorCodes.VersionConflict,
                "The readiness observation lost a concurrent Supply race.",
                etag: "\"v3\"");
        using HttpRequestMessage stalePatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                account_bindings = Array.Empty<object>(),
                reason = "remove stale readiness inputs",
            },
            "application/merge-patch+json",
            "supply-stale-candidate",
            "\"v2\"");
        using HttpResponseMessage raced = await admin.SendAsync(
            stalePatch,
            TestContext.Current.CancellationToken);

        Assert.Equal("\"v3\"", raced.Headers.ETag?.Tag);
        await AssertProblemAsync(
            raced,
            HttpStatusCode.PreconditionFailed,
            "version_conflict",
            expectedRetryable: true).ConfigureAwait(true);
        PatchGroupSupplyConfigurationCommand staleCommand =
            Assert.IsType<PatchGroupSupplyConfigurationCommand>(
                factory.UseCases.LastPatchConfiguration);
        Assert.Equal(2, staleCommand.ExpectedVersion);
        Assert.False(staleCommand.ChannelSpecified);
        Assert.True(staleCommand.AccountBindingsSpecified);
        Assert.Empty(staleCommand.AccountBindings!);
    }

    [Fact]
    public async Task ConfigurationBindingsAllowEmptyAndRejectDuplicateInvalidOrProviderMismatch()
    {
        await using SupplyApiFactory factory = new();
        using HttpClient admin = AuthenticatedClient(factory, "admin");

        using HttpRequestMessage empty = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = (Guid?)null,
                account_bindings = Array.Empty<object>(),
            },
            idempotencyKey: "bindings-empty");
        using HttpResponseMessage emptyResponse = await admin.SendAsync(
            empty,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, emptyResponse.StatusCode);
        Assert.Equal(1, factory.UseCases.CreateConfigurationCalls);
        Assert.Empty(
            Assert.IsType<CreateGroupSupplyConfigurationCommand>(
                factory.UseCases.LastCreateConfiguration).AccountBindings);

        object validBinding = new
        {
            account_id = AccountId.Value,
            enabled = true,
            priority_override = (int?)null,
            weight_override = (int?)null,
        };
        using HttpRequestMessage duplicate = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = ChannelId.Value,
                account_bindings = new[] { validBinding, validBinding },
            },
            idempotencyKey: "bindings-duplicate");
        using HttpResponseMessage duplicateResponse = await admin.SendAsync(
            duplicate,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            duplicateResponse,
            HttpStatusCode.UnprocessableEntity,
            "validation_failed",
            "/account_bindings").ConfigureAwait(true);
        Assert.Equal(1, factory.UseCases.CreateConfigurationCalls);

        using HttpRequestMessage invalid = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = ChannelId.Value,
                account_bindings = new[]
                {
                    new
                    {
                        account_id = Guid.Empty,
                        enabled = true,
                        priority_override = 100001,
                        weight_override = 0,
                    },
                },
            },
            idempotencyKey: "bindings-invalid");
        using HttpResponseMessage invalidResponse = await admin.SendAsync(
            invalid,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            invalidResponse,
            HttpStatusCode.UnprocessableEntity,
            "validation_failed",
            "/account_bindings/0/account_id").ConfigureAwait(true);
        using (JsonDocument invalidProblem = await ReadJsonAsync(
                   invalidResponse).ConfigureAwait(true))
        {
            JsonElement errors = invalidProblem.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty(
                "/account_bindings/0/priority_override",
                out _));
            Assert.True(errors.TryGetProperty(
                "/account_bindings/0/weight_override",
                out _));
        }
        Assert.Equal(1, factory.UseCases.CreateConfigurationCalls);

        factory.UseCases.PatchConfigurationResult =
            Result.Failure<SupplyCommandOutcome<GroupSupplyConfigurationView>>(
                "group_account_binding_invalid",
                "The Account provider does not match the configured Channel provider.");
        using HttpRequestMessage providerMismatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = ChannelId.Value,
                account_bindings = new[] { validBinding },
                reason = "bind provider candidate",
            },
            "application/merge-patch+json",
            "bindings-provider-mismatch",
            "\"v1\"");
        using HttpResponseMessage providerMismatchResponse = await admin.SendAsync(
            providerMismatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            providerMismatchResponse,
            HttpStatusCode.UnprocessableEntity,
            "group_account_binding_invalid").ConfigureAwait(true);
    }

    [Fact]
    public async Task SupplyTransportRequiresFrozenMediaTypesAndConcurrencyHeaders()
    {
        await using SupplyApiFactory factory = new();
        using HttpClient admin = AuthenticatedClient(factory, "admin");

        using HttpRequestMessage wrongChannelCreateContent = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/channels",
            ValidChannelCreate(),
            "application/problem+json",
            "channel-wrong-content");
        using HttpResponseMessage wrongChannelCreate = await admin.SendAsync(
            wrongChannelCreateContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongChannelCreate,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type").ConfigureAwait(true);

        using HttpRequestMessage channelMissingKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/channels",
            ValidChannelCreate());
        using HttpResponseMessage channelKeyRequired = await admin.SendAsync(
            channelMissingKey,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            channelKeyRequired,
            HttpStatusCode.PreconditionRequired,
            "idempotency_key_required").ConfigureAwait(true);

        using HttpRequestMessage wrongChannelPatchContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            new { name = "Renamed" },
            "application/json",
            "channel-wrong-patch-content",
            "\"v1\"");
        using HttpResponseMessage wrongChannelPatch = await admin.SendAsync(
            wrongChannelPatchContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongChannelPatch,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type").ConfigureAwait(true);

        using HttpRequestMessage channelPatchMissingMatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            new { name = "Renamed" },
            "application/merge-patch+json",
            "channel-missing-match");
        using HttpResponseMessage channelMatchRequired = await admin.SendAsync(
            channelPatchMissingMatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            channelMatchRequired,
            HttpStatusCode.PreconditionRequired,
            "if_match_required").ConfigureAwait(true);

        using HttpRequestMessage channelPatchWeakMatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/channels/{ChannelId.Value:D}",
            new { name = "Renamed" },
            "application/merge-patch+json",
            "channel-weak-match",
            "W/\"v1\"");
        using HttpResponseMessage weakChannelMatch = await admin.SendAsync(
            channelPatchWeakMatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            weakChannelMatch,
            HttpStatusCode.BadRequest,
            "invalid_request",
            "/headers/If-Match").ConfigureAwait(true);

        using HttpRequestMessage retireWithoutReason = new(
            HttpMethod.Delete,
            $"/api/v1/admin/channels/{ChannelId.Value:D}");
        retireWithoutReason.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            "channel-retire-no-reason");
        retireWithoutReason.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        using HttpResponseMessage reasonRequired = await admin.SendAsync(
            retireWithoutReason,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            reasonRequired,
            HttpStatusCode.BadRequest,
            "invalid_request",
            "/headers/X-Change-Reason").ConfigureAwait(true);

        using HttpRequestMessage wrongConfigurationCreateContent = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = (Guid?)null,
                account_bindings = Array.Empty<object>(),
            },
            "application/problem+json",
            "supply-wrong-content");
        using HttpResponseMessage wrongConfigurationCreate = await admin.SendAsync(
            wrongConfigurationCreateContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongConfigurationCreate,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type").ConfigureAwait(true);

        using HttpRequestMessage configurationMissingKey = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                channel_id = (Guid?)null,
                account_bindings = Array.Empty<object>(),
            });
        using HttpResponseMessage configurationKeyRequired = await admin.SendAsync(
            configurationMissingKey,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            configurationKeyRequired,
            HttpStatusCode.PreconditionRequired,
            "idempotency_key_required").ConfigureAwait(true);

        using HttpRequestMessage wrongConfigurationPatchContent = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                account_bindings = Array.Empty<object>(),
                reason = "clear staged bindings",
            },
            "application/json",
            "supply-wrong-patch-content",
            "\"v1\"");
        using HttpResponseMessage wrongConfigurationPatch = await admin.SendAsync(
            wrongConfigurationPatchContent,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            wrongConfigurationPatch,
            HttpStatusCode.UnsupportedMediaType,
            "unsupported_media_type").ConfigureAwait(true);

        using HttpRequestMessage configurationPatchMissingKey = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                account_bindings = Array.Empty<object>(),
                reason = "clear staged bindings",
            },
            "application/merge-patch+json",
            ifMatch: "\"v1\"");
        using HttpResponseMessage configurationKeyMissing = await admin.SendAsync(
            configurationPatchMissingKey,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            configurationKeyMissing,
            HttpStatusCode.PreconditionRequired,
            "idempotency_key_required").ConfigureAwait(true);

        using HttpRequestMessage configurationPatchMissingMatch = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{GroupId.Value:D}/supply-configuration",
            new
            {
                account_bindings = Array.Empty<object>(),
                reason = "clear staged bindings",
            },
            "application/merge-patch+json",
            "supply-missing-match");
        using HttpResponseMessage configurationMatchMissing = await admin.SendAsync(
            configurationPatchMissingMatch,
            TestContext.Current.CancellationToken);
        await AssertProblemAsync(
            configurationMatchMissing,
            HttpStatusCode.PreconditionRequired,
            "if_match_required").ConfigureAwait(true);
    }

    [Fact]
    public async Task ChannelValidationFailuresAndProjectionBranchesRemainFrozen()
    {
        await using SupplyApiFactory factory = new();
        using HttpClient auditor = AuthenticatedClient(factory, "auditor");

        using (HttpResponseMessage badLimit = await auditor.GetAsync(
                   "/api/v1/admin/channels?limit=0",
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                badLimit,
                HttpStatusCode.BadRequest,
                "invalid_request",
                "/limit").ConfigureAwait(true);
        }

        factory.UseCases.ListChannelResult = Result.Failure<ChannelPage>(
            "dependency_unavailable",
            "synthetic Channel list dependency");
        using (HttpResponseMessage failedList = await auditor.GetAsync(
                   "/api/v1/admin/channels",
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                failedList,
                HttpStatusCode.ServiceUnavailable,
                "dependency_unavailable",
                expectedRetryable: true).ConfigureAwait(true);
        }

        using (HttpResponseMessage emptyId = await auditor.GetAsync(
                   $"/api/v1/admin/channels/{Guid.Empty:D}",
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                emptyId,
                HttpStatusCode.BadRequest,
                "invalid_request",
                "/channelId").ConfigureAwait(true);
        }

        factory.UseCases.GetChannelResult = Result.Failure<ChannelView>(
            "resource_not_found",
            "synthetic missing Channel");
        using (HttpResponseMessage failedGet = await auditor.GetAsync(
                   $"/api/v1/admin/channels/{ChannelId.Value:D}",
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                failedGet,
                HttpStatusCode.NotFound,
                "resource_not_found").ConfigureAwait(true);
        }

        using HttpClient admin = AuthenticatedClient(factory, "admin");
        using (HttpRequestMessage invalidCreate = JsonCommand(
                   HttpMethod.Post,
                   "/api/v1/admin/channels",
                   new
                   {
                       name = "",
                       provider = "openai",
                       capabilities = (object?)null,
                       model_mappings = (object?)null,
                   },
                   idempotencyKey: "invalid-channel-create"))
        using (HttpResponseMessage response = await admin.SendAsync(
                   invalidCreate,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "validation_failed",
                "/name").ConfigureAwait(true);
        }

        factory.UseCases.CreateChannelResult =
            Result.Failure<SupplyCommandOutcome<ChannelView>>(
                "resource_conflict",
                "synthetic create conflict");
        using (HttpRequestMessage failedCreate = JsonCommand(
                   HttpMethod.Post,
                   "/api/v1/admin/channels",
                   ValidChannelCreate(),
                   idempotencyKey: "failed-channel-create"))
        using (HttpResponseMessage response = await admin.SendAsync(
                   failedCreate,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.Conflict,
                "resource_conflict").ConfigureAwait(true);
        }

        using (HttpRequestMessage emptyUpdateId = JsonCommand(
                   HttpMethod.Patch,
                   $"/api/v1/admin/channels/{Guid.Empty:D}",
                   new { name = "Renamed" },
                   "application/merge-patch+json",
                   "empty-channel-id",
                   "\"v1\""))
        using (HttpResponseMessage response = await admin.SendAsync(
                   emptyUpdateId,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                "invalid_request",
                "/channelId").ConfigureAwait(true);
        }

        using (HttpRequestMessage emptyUpdate = JsonCommand(
                   HttpMethod.Patch,
                   $"/api/v1/admin/channels/{ChannelId.Value:D}",
                   new Dictionary<string, object?>(StringComparer.Ordinal),
                   "application/merge-patch+json",
                   "empty-channel-update",
                   "\"v1\""))
        using (HttpResponseMessage response = await admin.SendAsync(
                   emptyUpdate,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "validation_failed",
                "/").ConfigureAwait(true);
        }

        using (HttpRequestMessage invalidUpdate = JsonCommand(
                   HttpMethod.Patch,
                   $"/api/v1/admin/channels/{ChannelId.Value:D}",
                   new
                   {
                       name = "",
                       status = "retired",
                       capabilities = (object?)null,
                       model_mappings = new object?[] { null },
                       reason = " ",
                   },
                   "application/merge-patch+json",
                   "invalid-channel-update",
                   "\"v1\""))
        using (HttpResponseMessage response = await admin.SendAsync(
                   invalidUpdate,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.UnprocessableEntity,
                "validation_failed",
                "/status").ConfigureAwait(true);
        }

        factory.UseCases.UpdateChannelResult =
            Result.Failure<SupplyCommandOutcome<ChannelView>>(
                "version_conflict",
                "synthetic stale Channel",
                etag: "\"v8\"");
        using (HttpRequestMessage failedUpdate = JsonCommand(
                   HttpMethod.Patch,
                   $"/api/v1/admin/channels/{ChannelId.Value:D}",
                   new { name = "Valid rename" },
                   "application/merge-patch+json",
                   "failed-channel-update",
                   "\"v7\""))
        using (HttpResponseMessage response = await admin.SendAsync(
                   failedUpdate,
                   TestContext.Current.CancellationToken))
        {
            await AssertProblemAsync(
                response,
                HttpStatusCode.PreconditionFailed,
                "version_conflict",
                expectedRetryable: true).ConfigureAwait(true);
            Assert.Equal("\"v8\"", response.Headers.ETag?.Tag);
        }

        factory.UseCases.ListChannelResult = Result.Success(new ChannelPage(
            [
                ChannelViewOf(
                    ChannelLifecycle.Retired,
                    version: 9,
                    provider: UpstreamProvider.OpenAi),
            ],
            NextCursor: null,
            HasMore: false));
        using HttpClient projectionAuditor = AuthenticatedClient(
            factory,
            "auditor");
        using HttpResponseMessage projected = await projectionAuditor.GetAsync(
            "/api/v1/admin/channels",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, projected.StatusCode);
    }

    [Fact]
    public async Task ChannelRetirementReferenceConflictMapsFrozenError()
    {
        await using SupplyApiFactory factory = new();
        factory.UseCases.RetireChannelResult =
            Result.Failure<SupplyCommandOutcome>(
                SupplyControlErrorCodes.ChannelInUse,
                "The Channel is referenced by a non-null Supply configuration.");
        using HttpClient client = AuthenticatedClient(factory, "operator");
        using HttpRequestMessage request = new(
            HttpMethod.Delete,
            $"/api/v1/admin/channels/{ChannelId.Value:D}");
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            "channel-in-use");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        request.Headers.TryAddWithoutValidation(
            "X-Change-Reason",
            "retire referenced channel");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            "channel_in_use").ConfigureAwait(true);
        RetireChannelCommand command = Assert.IsType<RetireChannelCommand>(
            factory.UseCases.LastRetireChannel);
        Assert.Equal(ChannelId, command.ChannelId);
        Assert.Equal(7, command.ExpectedVersion);
        Assert.Equal("retire referenced channel", command.Reason);
    }

    private static object ValidChannelCreate() => new
    {
        name = "Primary",
        provider = "openai_compatible",
        capabilities = new
        {
            responses = true,
            chat_completions = true,
            function_tools = true,
            streaming = true,
        },
        model_mappings = new[]
        {
            new
            {
                client_model = "gpt-5.1",
                upstream_model = "upstream-gpt-5.1",
            },
        },
    };

    private static ChannelView ChannelViewOf(
        ChannelLifecycle lifecycle,
        long version,
        UpstreamProvider provider = UpstreamProvider.OpenAiCompatible) => new(
        ChannelId,
        "Primary",
        provider,
        lifecycle,
        new ChannelCapabilitiesSnapshot(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true),
        [new ChannelModelMappingView("gpt-5.1", "upstream-gpt-5.1")],
        version,
        Timestamp,
        Timestamp.AddMinutes(version));

    private static GroupSupplyConfigurationView ConfigurationView(
        long version,
        EntityId? channelId,
        IReadOnlyList<GroupSupplyBindingView> bindings) => new(
        GroupId,
        channelId,
        bindings,
        version,
        Timestamp,
        Timestamp.AddMinutes(version));

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

    private static HttpClient AuthenticatedClient(
        SupplyApiFactory factory,
        string role)
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

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) => JsonDocument.Parse(
        await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(false));

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string? pointer = null,
        bool expectedRetryable = false)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = await ReadJsonAsync(response)
            .ConfigureAwait(false);
        JsonElement problem = document.RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal(
            expectedRetryable,
            problem.GetProperty("retryable").GetBoolean());
        Assert.True(Guid.TryParse(
            problem.GetProperty("request_id").GetString(),
            out _));
        if (pointer is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(
                pointer,
                out JsonElement messages));
            Assert.NotEmpty(messages.EnumerateArray());
        }
    }

    private sealed class SupplyApiFactory : PoolAiApiFactory
    {
        internal FakeSupplyUseCases UseCases { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IListChannelsUseCase>();
                services.RemoveAll<IGetChannelUseCase>();
                services.RemoveAll<ICreateChannelUseCase>();
                services.RemoveAll<IUpdateChannelUseCase>();
                services.RemoveAll<IRetireChannelUseCase>();
                services.RemoveAll<IGetGroupSupplyConfigurationUseCase>();
                services.RemoveAll<ICreateGroupSupplyConfigurationUseCase>();
                services.RemoveAll<IPatchGroupSupplyConfigurationUseCase>();
                services.AddSingleton<IListChannelsUseCase>(UseCases);
                services.AddSingleton<IGetChannelUseCase>(UseCases);
                services.AddSingleton<ICreateChannelUseCase>(UseCases);
                services.AddSingleton<IUpdateChannelUseCase>(UseCases);
                services.AddSingleton<IRetireChannelUseCase>(UseCases);
                services.AddSingleton<IGetGroupSupplyConfigurationUseCase>(UseCases);
                services.AddSingleton<ICreateGroupSupplyConfigurationUseCase>(UseCases);
                services.AddSingleton<IPatchGroupSupplyConfigurationUseCase>(UseCases);
            });
        }
    }

    private sealed class FakeSupplyUseCases :
        IListChannelsUseCase,
        IGetChannelUseCase,
        ICreateChannelUseCase,
        IUpdateChannelUseCase,
        IRetireChannelUseCase,
        IGetGroupSupplyConfigurationUseCase,
        ICreateGroupSupplyConfigurationUseCase,
        IPatchGroupSupplyConfigurationUseCase
    {
        internal Result<ChannelPage> ListChannelResult { get; set; } =
            Result.Success(new ChannelPage(
                [ChannelViewOf(ChannelLifecycle.Disabled, version: 1)],
                "next-channel",
                HasMore: true));

        internal Result<ChannelView> GetChannelResult { get; set; } =
            Result.Success(ChannelViewOf(ChannelLifecycle.Disabled, version: 1));

        internal Result<SupplyCommandOutcome<ChannelView>> CreateChannelResult
            { get; set; } = Result.Success(new SupplyCommandOutcome<ChannelView>(
                StatusCodes.Status201Created,
                IsReplay: false,
                ChannelViewOf(ChannelLifecycle.Disabled, version: 1),
                "\"v1\""));

        internal Result<SupplyCommandOutcome<ChannelView>> UpdateChannelResult
            { get; set; } = Result.Success(new SupplyCommandOutcome<ChannelView>(
                StatusCodes.Status200OK,
                IsReplay: false,
                ChannelViewOf(ChannelLifecycle.Disabled, version: 2),
                "\"v2\""));

        internal Result<SupplyCommandOutcome> RetireChannelResult { get; set; } =
            Result.Success(new SupplyCommandOutcome(
                StatusCodes.Status204NoContent,
                IsReplay: false,
                "\"v2\""));

        internal Result<GroupSupplyConfigurationView> GetConfigurationResult
            { get; set; } = Result.Success(ConfigurationView(
                version: 1,
                channelId: null,
                bindings: []));

        internal Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>
            CreateConfigurationResult { get; set; } =
            Result.Success(
                new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                    StatusCodes.Status201Created,
                    IsReplay: false,
                    ConfigurationView(
                        version: 1,
                        channelId: null,
                        bindings: []),
                    "\"v1\""));

        internal Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>
            PatchConfigurationResult { get; set; } =
            Result.Success(
                new SupplyCommandOutcome<GroupSupplyConfigurationView>(
                    StatusCodes.Status200OK,
                    IsReplay: false,
                    ConfigurationView(
                        version: 2,
                        channelId: null,
                        bindings: []),
                    "\"v2\""));

        internal ListChannelsQuery? LastListChannel { get; private set; }

        internal GetChannelQuery? LastGetChannel { get; private set; }

        internal CreateChannelCommand? LastCreateChannel { get; private set; }

        internal UpdateChannelCommand? LastUpdateChannel { get; private set; }

        internal RetireChannelCommand? LastRetireChannel { get; private set; }

        internal GetGroupSupplyConfigurationQuery? LastGetConfiguration
            { get; private set; }

        internal CreateGroupSupplyConfigurationCommand? LastCreateConfiguration
            { get; private set; }

        internal PatchGroupSupplyConfigurationCommand? LastPatchConfiguration
            { get; private set; }

        internal int CreateConfigurationCalls { get; private set; }

        public ValueTask<Result<ChannelPage>> ExecuteAsync(
            ListChannelsQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastListChannel = query;
            return ValueTask.FromResult(ListChannelResult);
        }

        public ValueTask<Result<ChannelView>> ExecuteAsync(
            GetChannelQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastGetChannel = query;
            return ValueTask.FromResult(GetChannelResult);
        }

        public ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
            CreateChannelCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCreateChannel = command;
            return ValueTask.FromResult(CreateChannelResult);
        }

        public ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
            UpdateChannelCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUpdateChannel = command;
            return ValueTask.FromResult(UpdateChannelResult);
        }

        public ValueTask<Result<SupplyCommandOutcome>> ExecuteAsync(
            RetireChannelCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRetireChannel = command;
            return ValueTask.FromResult(RetireChannelResult);
        }

        public ValueTask<Result<GroupSupplyConfigurationView>> ExecuteAsync(
            GetGroupSupplyConfigurationQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastGetConfiguration = query;
            return ValueTask.FromResult(GetConfigurationResult);
        }

        public ValueTask<
            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
            CreateGroupSupplyConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateConfigurationCalls++;
            LastCreateConfiguration = command;
            return ValueTask.FromResult(CreateConfigurationResult);
        }

        public ValueTask<
            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
            PatchGroupSupplyConfigurationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPatchConfiguration = command;
            return ValueTask.FromResult(PatchConfigurationResult);
        }
    }
}
#pragma warning restore MA0051
