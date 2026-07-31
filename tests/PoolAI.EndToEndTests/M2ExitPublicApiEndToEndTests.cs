using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Routing.Abstractions;

#pragma warning disable MA0051 // M2 Exit keeps the complete public HTTP proof visible.

namespace PoolAI.EndToEndTests;

[Collection(M2ExitSerialTestGroup.Name)]
public sealed class M2ExitPublicApiEndToEndTests
{
    private const string UserPassword = "M2-Exit-User-Password-123!";
    private const string UpstreamCredential = "m2-exit-upstream-credential";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task M2ExitUsesPublicControlPlaneProductionReadinessAndRouter()
    {
        // Governing contract: the M2 exit gate starts from migrations plus the
        // bootstrap Admin, creates every business fact through public HTTP, and
        // uses the production readiness and Router graphs with real PostgreSQL
        // and Redis. The only upstream seam answers GET /models for health.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using LoopbackModelsUpstream upstream = new();
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(
                cancellationToken).ConfigureAwait(true);

        string adminToken = await LoginAsync(
            environment,
            environment.AdminEmail,
            PasswordResetHttpEndToEndEnvironment.OriginalPassword,
            cancellationToken).ConfigureAwait(true);
        await AssertClosedRegistrationLeavesOnlyBootstrapAdminAsync(
            environment,
            cancellationToken).ConfigureAwait(true);
        UserFixture user = await CreateUserAndLoginAsync(
            environment,
            adminToken,
            cancellationToken).ConfigureAwait(true);
        AccessFixture access = await CreateDisabledAccessResourcesAsync(
            environment,
            adminToken,
            cancellationToken).ConfigureAwait(true);

        await AssertDisabledGroupFailsClosedAsync(
            environment,
            adminToken,
            user,
            access,
            cancellationToken).ConfigureAwait(true);
        SupplyFixture supply = await ProvisionSupplyThroughPublicApiAsync(
            environment,
            adminToken,
            access.GroupId,
            upstream.BaseAddress,
            cancellationToken).ConfigureAwait(true);
        await AssertConfiguredButUnhealthyGroupFailsClosedAsync(
            environment,
            adminToken,
            access.GroupId,
            supply,
            cancellationToken).ConfigureAwait(true);
        using (IHost healthHost = environment.CreateSupplyHealthHost())
        {
            // Run one deterministic round through the registered production
            // hosted service. Worker lifecycle and retry mapping stay covered
            // by the focused Worker tests.
            await PasswordResetHttpEndToEndEnvironment.RunSupplyHealthRoundAsync(
                healthHost,
                cancellationToken).ConfigureAwait(true);
            long healthyAccountVersion = await WaitForAccountHealthAsync(
                environment,
                adminToken,
                supply.AccountId,
                "healthy",
                cancellationToken).ConfigureAwait(true);
            supply = supply with { AccountVersion = healthyAccountVersion };
        }

        await ActivateGroupAsync(
            environment,
            adminToken,
            access.GroupId,
            cancellationToken).ConfigureAwait(true);

        SubscriptionFixture subscription = await AssignSubscriptionAsync(
            environment,
            adminToken,
            user,
            access,
            cancellationToken).ConfigureAwait(true);
        ApiKeyFixture apiKey = await CreateReplayAndReadApiKeyAsync(
            environment,
            user,
            access.GroupId,
            cancellationToken).ConfigureAwait(true);
        long routedAccountVersion = await AssertProductionRouterLeaseAsync(
            environment,
            adminToken,
            access.GroupId,
            supply,
            cancellationToken).ConfigureAwait(true);
        supply = supply with { AccountVersion = routedAccountVersion };
        await AssertRevocationAndCanonicalAuthorizationAsync(
            environment,
            adminToken,
            user,
            subscription,
            apiKey,
            cancellationToken).ConfigureAwait(true);
        await AssertPersistedJourneyFactsAsync(
            environment,
            user.UserId,
            access,
            supply,
            subscription.SubscriptionId,
            apiKey.ApiKeyId,
            cancellationToken).ConfigureAwait(true);
        await upstream.AssertModelsOnlyAsync(cancellationToken)
            .ConfigureAwait(true);
    }

    private static async ValueTask AssertClosedRegistrationLeavesOnlyBootstrapAdminAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        CancellationToken cancellationToken)
    {
        long usersBefore = await CountUsersAsync(environment, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(1, usersBefore);
        const string CandidateEmail = "anonymous-registration@poolai.test";

        using HttpResponseMessage apiResponse = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new
            {
                email = CandidateEmail,
                password = UserPassword,
            },
            cancellationToken).ConfigureAwait(false);
        using HttpResponseMessage rootResponse = await environment.Client.PostAsJsonAsync(
            "/register",
            new
            {
                email = CandidateEmail,
                password = UserPassword,
            },
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.NotFound, apiResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
        Assert.Equal(
            usersBefore,
            await CountUsersAsync(environment, cancellationToken).ConfigureAwait(false));
        Assert.Equal(
            0,
            await environment.CountUsersByNormalizedEmailAsync(
                CandidateEmail,
                cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<long> CountUsersAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand(
            "SELECT count(*) FROM public.users;");
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Assert.IsType<long>(value);
    }

    private static async ValueTask<UserFixture> CreateUserAndLoginAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        CancellationToken cancellationToken)
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string email = $"m2-exit-{suffix}@poolai.test";
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/users",
            adminToken,
            new
            {
                email,
                display_name = "M2 Exit User",
                role = "user",
                temporary_password = UserPassword,
            },
            idempotencyKey: "m2-exit-user-create");
        using HttpResponseMessage response = await environment.Client.SendAsync(
            create,
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v2\"", response.Headers.ETag?.Tag);
        using JsonDocument created = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Guid userId = created.RootElement.GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, userId);
        Assert.Equal("active", created.RootElement.GetProperty("status").GetString());
        Assert.Equal("user", created.RootElement.GetProperty("role").GetString());

        string userToken = await LoginAsync(
            environment,
            email,
            UserPassword,
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage profileRequest = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/me",
            userToken);
        using HttpResponseMessage profile = await environment.Client.SendAsync(
            profileRequest,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using JsonDocument profileJson = await ReadJsonAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(userId, profileJson.RootElement.GetProperty("id").GetGuid());
        return new UserFixture(userId, userToken);
    }

    private static async ValueTask<AccessFixture> CreateDisabledAccessResourcesAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage createGroup = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/groups",
            adminToken,
            new
            {
                name = $"M2 Exit {Guid.NewGuid():N}",
                description = "M2 public API acceptance",
                total_tokens = 1_000_000,
            },
            idempotencyKey: "m2-exit-group-create");
        using HttpResponseMessage groupResponse = await environment.Client.SendAsync(
            createGroup,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        Assert.Equal("\"v1\"", groupResponse.Headers.ETag?.Tag);
        using JsonDocument group = await ReadJsonAsync(groupResponse, cancellationToken)
            .ConfigureAwait(false);
        Guid groupId = group.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("disabled", group.RootElement.GetProperty("status").GetString());

        using HttpRequestMessage createTemplate = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscription-templates",
            adminToken,
            new
            {
                group_id = groupId,
                name = "M2 Exit Access",
                default_duration_days = 30,
            },
            idempotencyKey: "m2-exit-template-create");
        using HttpResponseMessage templateResponse = await environment.Client.SendAsync(
            createTemplate,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, templateResponse.StatusCode);
        Assert.Equal("\"v1\"", templateResponse.Headers.ETag?.Tag);
        using JsonDocument template = await ReadJsonAsync(templateResponse, cancellationToken)
            .ConfigureAwait(false);
        return new AccessFixture(
            groupId,
            template.RootElement.GetProperty("id").GetGuid());
    }

    private static async ValueTask AssertDisabledGroupFailsClosedAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        UserFixture user,
        AccessFixture access,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage assign = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            adminToken,
            new
            {
                user_id = user.UserId,
                template_id = access.TemplateId,
                reason = "must remain disabled before Supply readiness",
            },
            idempotencyKey: "m2-exit-disabled-assignment");
        using HttpResponseMessage assignmentResponse = await environment.Client.SendAsync(
            assign,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            assignmentResponse,
            HttpStatusCode.Forbidden,
            "group_disabled",
            cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage activate = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{access.GroupId:D}",
            adminToken,
            new { status = "active", reason = "readiness must be observed" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-activation-not-ready",
            ifMatch: "\"v1\"");
        using HttpResponseMessage activationResponse = await environment.Client.SendAsync(
            activate,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            activationResponse,
            HttpStatusCode.Conflict,
            "group_activation_not_ready",
            cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage forbidden = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/admin/groups",
            user.AccessToken);
        using HttpResponseMessage forbiddenResponse = await environment.Client.SendAsync(
            forbidden,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            forbiddenResponse,
            HttpStatusCode.Forbidden,
            "role_required",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask AssertConfiguredButUnhealthyGroupFailsClosedAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid groupId,
        SupplyFixture supply,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage configuration = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/admin/groups/{groupId:D}/supply-configuration",
            adminToken);
        using HttpResponseMessage configurationResponse =
            await environment.Client.SendAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
        Assert.Equal(
            $"\"v{supply.ConfigurationVersion}\"",
            configurationResponse.Headers.ETag?.Tag);

        using HttpRequestMessage activate = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{groupId:D}",
            adminToken,
            new { status = "active", reason = "health evidence must be current" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-activation-unhealthy",
            ifMatch: "\"v1\"");
        using HttpResponseMessage activationResponse = await environment.Client.SendAsync(
            activate,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            activationResponse,
            HttpStatusCode.Conflict,
            "group_activation_not_ready",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SupplyFixture> ProvisionSupplyThroughPublicApiAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid groupId,
        string upstreamBaseUrl,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage createAccount = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/accounts",
            adminToken,
            new
            {
                name = "M2 Exit Account",
                provider = "openai_compatible",
                base_url = upstreamBaseUrl,
                credential = UpstreamCredential,
                max_concurrency = 4,
                priority = 10,
                weight = 100,
            },
            idempotencyKey: "m2-exit-account-create");
        using HttpResponseMessage accountResponse = await environment.Client.SendAsync(
            createAccount,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, accountResponse.StatusCode);
        Assert.Equal("\"v1\"", accountResponse.Headers.ETag?.Tag);
        string accountBody = await accountResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.DoesNotContain(UpstreamCredential, accountBody, StringComparison.Ordinal);
        using JsonDocument account = JsonDocument.Parse(accountBody);
        Guid accountId = account.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("disabled", account.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "unknown",
            account.RootElement.GetProperty("health").GetProperty("status").GetString());

        using HttpRequestMessage activateAccount = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{accountId:D}",
            adminToken,
            new { status = "active", reason = "M2 Exit controlled health validation" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-account-activate",
            ifMatch: "\"v1\"");
        using HttpResponseMessage activatedAccount = await environment.Client.SendAsync(
            activateAccount,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, activatedAccount.StatusCode);
        Assert.Equal("\"v2\"", activatedAccount.Headers.ETag?.Tag);

        using HttpRequestMessage createChannel = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/channels",
            adminToken,
            new
            {
                name = "M2 Exit Channel",
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
                        client_model = "gpt-test",
                        upstream_model = "gpt-test",
                    },
                },
            },
            idempotencyKey: "m2-exit-channel-create");
        using HttpResponseMessage channelResponse = await environment.Client.SendAsync(
            createChannel,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, channelResponse.StatusCode);
        Assert.Equal("\"v1\"", channelResponse.Headers.ETag?.Tag);
        using JsonDocument channel = await ReadJsonAsync(channelResponse, cancellationToken)
            .ConfigureAwait(false);
        Guid channelId = channel.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("disabled", channel.RootElement.GetProperty("status").GetString());

        using HttpRequestMessage activateChannel = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/channels/{channelId:D}",
            adminToken,
            new { status = "active", reason = "M2 Exit model mapping validated" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-channel-activate",
            ifMatch: "\"v1\"");
        using HttpResponseMessage activatedChannel = await environment.Client.SendAsync(
            activateChannel,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, activatedChannel.StatusCode);
        Assert.Equal("\"v2\"", activatedChannel.Headers.ETag?.Tag);

        using HttpRequestMessage createConfiguration = JsonCommand(
            HttpMethod.Post,
            $"/api/v1/admin/groups/{groupId:D}/supply-configuration",
            adminToken,
            new
            {
                channel_id = channelId,
                account_bindings = new[]
                {
                    new
                    {
                        account_id = accountId,
                        enabled = true,
                        priority_override = (int?)null,
                        weight_override = (int?)null,
                    },
                },
            },
            idempotencyKey: "m2-exit-supply-configuration-create");
        using HttpResponseMessage configurationResponse = await environment.Client.SendAsync(
            createConfiguration,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, configurationResponse.StatusCode);
        using JsonDocument configuration = await ReadJsonAsync(
            configurationResponse,
            cancellationToken).ConfigureAwait(false);
        long configurationVersion =
            configuration.RootElement.GetProperty("version").GetInt64();
        Assert.True(configurationVersion > 1);
        Assert.Equal(
            $"\"v{configurationVersion}\"",
            configurationResponse.Headers.ETag?.Tag);
        Assert.Equal(groupId, configuration.RootElement.GetProperty("group_id").GetGuid());
        Assert.Equal(channelId, configuration.RootElement.GetProperty("channel_id").GetGuid());
        JsonElement binding = Assert.Single(
            configuration.RootElement.GetProperty("account_bindings")
                .EnumerateArray().ToArray());
        Assert.Equal(accountId, binding.GetProperty("account_id").GetGuid());
        Assert.True(binding.GetProperty("enabled").GetBoolean());
        return new SupplyFixture(
            accountId,
            channelId,
            AccountVersion: 2,
            ConfigurationVersion: configurationVersion);
    }

    private static async ValueTask<long> WaitForAccountHealthAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid accountId,
        string expectedHealth,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(20);
        string lastBody = string.Empty;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            using HttpRequestMessage get = AuthorizedRequest(
                HttpMethod.Get,
                $"/api/v1/admin/accounts/{accountId:D}",
                adminToken);
            using HttpResponseMessage response = await environment.Client.SendAsync(
                get,
                cancellationToken).ConfigureAwait(false);
            lastBody = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument account = JsonDocument.Parse(lastBody);
            if (string.Equals(
                    expectedHealth,
                    account.RootElement.GetProperty("health")
                        .GetProperty("status").GetString(),
                    StringComparison.Ordinal))
            {
                Assert.Equal("active", account.RootElement.GetProperty("status").GetString());
                long version = account.RootElement.GetProperty("version").GetInt64();
                Assert.True(version > 2);
                return version;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Fail(
            $"Account {accountId:D} did not become {expectedHealth}. Last response: {lastBody}");
        return 0;
    }

    private static async ValueTask<int> ReadAccountActiveLeasesAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage get = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/admin/accounts/{accountId:D}",
            adminToken);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            get,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument account = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return account.RootElement.GetProperty("active_leases").GetInt32();
    }

    private static async ValueTask WaitForAccountActiveLeasesAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid accountId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = TimeProvider.System.GetUtcNow().AddSeconds(5);
        int actual = -1;
        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            actual = await ReadAccountActiveLeasesAsync(
                environment,
                adminToken,
                accountId,
                cancellationToken).ConfigureAwait(false);
            if (actual == expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken)
                .ConfigureAwait(false);
        }

        Assert.Equal(expectedCount, actual);
    }

    private static async ValueTask<long> UpdateAccountWhileLeasedAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid accountId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        object body = new
        {
            priority = 11,
            reason = "M2 Exit live lease response proof",
        };
        string ifMatch = $"\"v{expectedVersion}\"";
        const string IdempotencyKey = "m2-exit-account-update-while-leased";
        using HttpRequestMessage update = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{accountId:D}",
            adminToken,
            body,
            contentType: "application/merge-patch+json",
            idempotencyKey: IdempotencyKey,
            ifMatch: ifMatch);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            update,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        long updatedVersion = expectedVersion + 1;
        Assert.Equal($"\"v{updatedVersion}\"", response.Headers.ETag?.Tag);
        using JsonDocument updated = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(1, updated.RootElement.GetProperty("active_leases").GetInt32());
        Assert.Equal(updatedVersion, updated.RootElement.GetProperty("version").GetInt64());

        using HttpRequestMessage replay = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/accounts/{accountId:D}",
            adminToken,
            body,
            contentType: "application/merge-patch+json",
            idempotencyKey: IdempotencyKey,
            ifMatch: ifMatch);
        using HttpResponseMessage replayResponse = await environment.Client.SendAsync(
            replay,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal($"\"v{updatedVersion}\"", replayResponse.Headers.ETag?.Tag);
        using JsonDocument replayed = await ReadJsonAsync(
            replayResponse,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(1, replayed.RootElement.GetProperty("active_leases").GetInt32());
        Assert.Equal(updatedVersion, replayed.RootElement.GetProperty("version").GetInt64());
        return updatedVersion;
    }

    private static async ValueTask<long> AssertProductionRouterLeaseAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid groupId,
        SupplyFixture supply,
        CancellationToken cancellationToken)
    {
        IAccountRouter router = environment.Services.GetRequiredService<IAccountRouter>();
        RouteAccountCommand firstCommand = new(
            new EntityId(groupId),
            "gpt-test",
            new EntityId(Guid.CreateVersion7()),
            new EntityId(Guid.CreateVersion7()),
            GroupPolicyVersion: 2,
            SessionAffinityHash: "0123456789abcdef0123456789abcdef");
        Result<IAccountLease> firstResult = await router.RouteAsync(
            firstCommand,
            cancellationToken).ConfigureAwait(false);
        Assert.True(
            firstResult.IsSuccess,
            firstResult.IsFailure ? firstResult.Error.Code : string.Empty);
        IAccountLease firstLease = firstResult.Value;
        await using ConfiguredAsyncDisposable firstLeaseLifetime =
            firstLease.ConfigureAwait(false);
        Assert.Equal(groupId, firstLease.Route.GroupId.Value);
        Assert.Equal(supply.ChannelId, firstLease.Route.ChannelId.Value);
        Assert.Equal(supply.AccountId, firstLease.Route.AccountId.Value);
        Assert.Equal(
            supply.ConfigurationVersion,
            firstLease.Route.SupplyConfigurationVersion);
        Assert.Equal(2, firstLease.Route.ChannelVersion);
        Assert.Equal(supply.AccountVersion, firstLease.Route.AccountVersion);
        Assert.Equal(
            1,
            await ReadAccountActiveLeasesAsync(
                environment,
                adminToken,
                supply.AccountId,
                cancellationToken).ConfigureAwait(false));
        long updatedAccountVersion = await UpdateAccountWhileLeasedAsync(
            environment,
            adminToken,
            supply.AccountId,
            supply.AccountVersion,
            cancellationToken).ConfigureAwait(false);

        Result<AccountRoute> renewed = await firstLease.RenewAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(
            renewed.IsSuccess,
            renewed.IsFailure ? renewed.Error.Code : string.Empty);
        Assert.Equal(supply.AccountId, renewed.Value.AccountId.Value);
        Result<bool> firstReleased = await firstLease.ReleaseAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(firstReleased.IsSuccess);
        Assert.True(firstReleased.Value);
        await WaitForAccountActiveLeasesAsync(
            environment,
            adminToken,
            supply.AccountId,
            expectedCount: 0,
            cancellationToken).ConfigureAwait(false);

        RouteAccountCommand secondCommand = firstCommand with
        {
            RequestId = new EntityId(Guid.CreateVersion7()),
            AttemptId = new EntityId(Guid.CreateVersion7()),
        };
        Result<IAccountLease> secondResult = await router.RouteAsync(
            secondCommand,
            cancellationToken).ConfigureAwait(false);
        Assert.True(
            secondResult.IsSuccess,
            secondResult.IsFailure ? secondResult.Error.Code : string.Empty);
        IAccountLease secondLease = secondResult.Value;
        await using ConfiguredAsyncDisposable secondLeaseLifetime =
            secondLease.ConfigureAwait(false);
        Assert.Equal(firstLease.Route.AccountId, secondLease.Route.AccountId);
        Assert.Equal(firstLease.Route.GroupId, secondLease.Route.GroupId);
        Assert.Equal(updatedAccountVersion, secondLease.Route.AccountVersion);
        Result<bool> secondReleased = await secondLease.ReleaseAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(secondReleased.IsSuccess);
        Assert.True(secondReleased.Value);
        await WaitForAccountActiveLeasesAsync(
            environment,
            adminToken,
            supply.AccountId,
            expectedCount: 0,
            cancellationToken).ConfigureAwait(false);
        return updatedAccountVersion;
    }

    private static async ValueTask ActivateGroupAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage activate = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/groups/{groupId:D}",
            adminToken,
            new { status = "active", reason = "database-observed Supply readiness" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-activation-ready",
            ifMatch: "\"v1\"");
        using HttpResponseMessage response = await environment.Client.SendAsync(
            activate,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v2\"", response.Headers.ETag?.Tag);
        using JsonDocument activated = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal("active", activated.RootElement.GetProperty("status").GetString());
        Assert.False(
            activated.RootElement.TryGetProperty(
                "activation_supply_readiness_token",
                out _));
        Assert.False(
            activated.RootElement.TryGetProperty(
                "activation_supply_readiness_observed_at",
                out _));
    }

    private static async ValueTask<SubscriptionFixture> AssignSubscriptionAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        UserFixture user,
        AccessFixture access,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage assign = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/subscriptions",
            adminToken,
            new
            {
                user_id = user.UserId,
                template_id = access.TemplateId,
                reason = "M2 access approval",
            },
            idempotencyKey: "m2-exit-subscription-assign");
        using HttpResponseMessage response = await environment.Client.SendAsync(
            assign,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
        using JsonDocument assigned = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Guid subscriptionId = assigned.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("active", assigned.RootElement.GetProperty("status").GetString());
        Assert.Equal("active", assigned.RootElement.GetProperty("effective_status").GetString());

        await AssertSelfSubscriptionStateAsync(
            environment,
            user.AccessToken,
            subscriptionId,
            "active",
            cancellationToken).ConfigureAwait(false);
        await AssertSelfGroupPoolAsync(
            environment,
            user.AccessToken,
            access.GroupId,
            subscriptionId,
            cancellationToken).ConfigureAwait(false);
        return new SubscriptionFixture(subscriptionId);
    }

    private static async ValueTask<ApiKeyFixture> CreateReplayAndReadApiKeyAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        UserFixture user,
        Guid groupId,
        CancellationToken cancellationToken)
    {
        object body = new
        {
            name = "M2 Exit key",
            group_id = groupId,
            allowed_cidrs = Array.Empty<string>(),
        };
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/me/api-keys",
            user.AccessToken,
            body,
            idempotencyKey: "m2-exit-api-key-create");
        using HttpResponseMessage response = await environment.Client.SendAsync(
            create,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"v1\"", response.Headers.ETag?.Tag);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using JsonDocument created = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        string secret = Assert.IsType<string>(
            created.RootElement.GetProperty("secret").GetString());
        Guid apiKeyId = created.RootElement.GetProperty("api_key").GetProperty("id").GetGuid();

        using HttpRequestMessage replay = JsonCommand(
            HttpMethod.Post,
            "/api/v1/me/api-keys",
            user.AccessToken,
            body,
            idempotencyKey: "m2-exit-api-key-create");
        using HttpResponseMessage replayResponse = await environment.Client.SendAsync(
            replay,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        using JsonDocument replayed = await ReadJsonAsync(replayResponse, cancellationToken)
            .ConfigureAwait(false);
        Assert.True(
            string.Equals(
                secret,
                replayed.RootElement.GetProperty("secret").GetString(),
                StringComparison.Ordinal),
            "API Key idempotent replay did not return the original secret.");
        Assert.Equal(
            apiKeyId,
            replayed.RootElement.GetProperty("api_key").GetProperty("id").GetGuid());

        using HttpRequestMessage list = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/me/api-keys",
            user.AccessToken);
        using HttpResponseMessage listResponse = await environment.Client.SendAsync(
            list,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using JsonDocument listed = await ReadJsonAsync(listResponse, cancellationToken)
            .ConfigureAwait(false);
        JsonElement item = Assert.Single(
            listed.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal(apiKeyId, item.GetProperty("id").GetGuid());
        Assert.False(item.TryGetProperty("secret", out _));

        using HttpRequestMessage get = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/me/api-keys/{apiKeyId:D}",
            user.AccessToken);
        using HttpResponseMessage getResponse = await environment.Client.SendAsync(
            get,
            cancellationToken).ConfigureAwait(false);
        string getBody = await getResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(
            getResponse.StatusCode == HttpStatusCode.OK,
            $"Expected API Key GET to succeed, got {(int)getResponse.StatusCode}: {getBody}");
        using JsonDocument read = JsonDocument.Parse(getBody);
        Assert.False(read.RootElement.TryGetProperty("secret", out _));
        return new ApiKeyFixture(apiKeyId);
    }

    private static async ValueTask AssertRevocationAndCanonicalAuthorizationAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        UserFixture user,
        SubscriptionFixture subscription,
        ApiKeyFixture apiKey,
        CancellationToken cancellationToken)
    {
        await UpdateSubscriptionStateAsync(
            environment,
            adminToken,
            subscription.SubscriptionId,
            "\"v1\"",
            "suspended",
            "M2 suspension proof",
            "m2-exit-subscription-suspend",
            "\"v2\"",
            cancellationToken).ConfigureAwait(false);
        await AssertSelfSubscriptionStateAsync(
            environment,
            user.AccessToken,
            subscription.SubscriptionId,
            "suspended",
            cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage deniedKey = JsonCommand(
            HttpMethod.Post,
            "/api/v1/me/api-keys",
            user.AccessToken,
            new
            {
                name = "must be denied",
                group_id = await ReadApiKeyGroupAsync(
                    environment,
                    user.AccessToken,
                    apiKey.ApiKeyId,
                    cancellationToken).ConfigureAwait(false),
                allowed_cidrs = Array.Empty<string>(),
            },
            idempotencyKey: "m2-exit-key-while-suspended");
        using HttpResponseMessage deniedResponse = await environment.Client.SendAsync(
            deniedKey,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            deniedResponse,
            HttpStatusCode.Forbidden,
            "subscription_inactive",
            cancellationToken).ConfigureAwait(false);

        await UpdateSubscriptionStateAsync(
            environment,
            adminToken,
            subscription.SubscriptionId,
            "\"v2\"",
            "active",
            "M2 restoration proof",
            "m2-exit-subscription-resume",
            "\"v3\"",
            cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage revoke = AuthorizedRequest(
            HttpMethod.Delete,
            $"/api/v1/me/api-keys/{apiKey.ApiKeyId:D}",
            user.AccessToken);
        revoke.Headers.TryAddWithoutValidation("Idempotency-Key", "m2-exit-api-key-revoke");
        revoke.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        revoke.Headers.TryAddWithoutValidation("X-Change-Reason", "M2 revocation proof");
        using HttpResponseMessage revokedResponse = await environment.Client.SendAsync(
            revoke,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NoContent, revokedResponse.StatusCode);
        Assert.Equal("\"v2\"", revokedResponse.Headers.ETag?.Tag);

        using HttpRequestMessage getRevoked = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/me/api-keys/{apiKey.ApiKeyId:D}",
            user.AccessToken);
        using HttpResponseMessage getRevokedResponse = await environment.Client.SendAsync(
            getRevoked,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, getRevokedResponse.StatusCode);
        using JsonDocument revoked = await ReadJsonAsync(getRevokedResponse, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal("revoked", revoked.RootElement.GetProperty("status").GetString());
        Assert.False(revoked.RootElement.TryGetProperty("secret", out _));

        string userEtag = await ReadUserEtagAsync(
            environment,
            adminToken,
            user.UserId,
            cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage disableUser = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{user.UserId:D}",
            adminToken,
            new { status = "disabled", reason = "M2 canonical revocation proof" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m2-exit-user-disable",
            ifMatch: userEtag);
        using HttpResponseMessage disabledResponse = await environment.Client.SendAsync(
            disableUser,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, disabledResponse.StatusCode);

        using HttpRequestMessage stale = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/me",
            user.AccessToken);
        using HttpResponseMessage staleResponse = await environment.Client.SendAsync(
            stale,
            cancellationToken).ConfigureAwait(false);
        await AssertProblemAsync(
            staleResponse,
            HttpStatusCode.Unauthorized,
            "invalid_user_token",
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask UpdateSubscriptionStateAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid subscriptionId,
        string ifMatch,
        string status,
        string reason,
        string idempotencyKey,
        string expectedEtag,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage update = JsonCommand(
            HttpMethod.Patch,
            $"/api/v1/admin/subscriptions/{subscriptionId:D}",
            adminToken,
            new { status, reason },
            contentType: "application/merge-patch+json",
            idempotencyKey: idempotencyKey,
            ifMatch: ifMatch);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            update,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedEtag, response.Headers.ETag?.Tag);
        using JsonDocument updated = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(status, updated.RootElement.GetProperty("status").GetString());
        Assert.Equal(status, updated.RootElement.GetProperty("effective_status").GetString());
    }

    private static async ValueTask AssertSelfSubscriptionStateAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string userToken,
        Guid subscriptionId,
        string expectedStatus,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/me/subscriptions",
            userToken);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        JsonElement item = Assert.Single(
            json.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal(subscriptionId, item.GetProperty("id").GetGuid());
        Assert.Equal(expectedStatus, item.GetProperty("effective_status").GetString());
    }

    private static async ValueTask AssertSelfGroupPoolAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string userToken,
        Guid groupId,
        Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = AuthorizedRequest(
            HttpMethod.Get,
            "/api/v1/me/group-pools",
            userToken);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        JsonElement pool = Assert.Single(
            json.RootElement.GetProperty("data").EnumerateArray().ToArray());
        Assert.Equal(groupId, pool.GetProperty("group_id").GetGuid());
        Assert.Equal(subscriptionId, pool.GetProperty("subscription_id").GetGuid());
        Assert.Equal("active", pool.GetProperty("quota_status").GetString());
        Assert.Equal("1000000", pool.GetProperty("total_tokens").GetString());
        Assert.Equal("0", pool.GetProperty("consumed_tokens").GetString());
        Assert.Equal("0", pool.GetProperty("reserved_tokens").GetString());
        Assert.Equal("1000000", pool.GetProperty("remaining_tokens").GetString());
    }

    private static async ValueTask<Guid> ReadApiKeyGroupAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string userToken,
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/me/api-keys/{apiKeyId:D}",
            userToken);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return json.RootElement.GetProperty("group_id").GetGuid();
    }

    private static async ValueTask<string> ReadUserEtagAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string adminToken,
        Guid userId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = AuthorizedRequest(
            HttpMethod.Get,
            $"/api/v1/admin/users/{userId:D}",
            adminToken);
        using HttpResponseMessage response = await environment.Client.SendAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<string>(response.Headers.ETag?.Tag);
    }

    private static async ValueTask AssertPersistedJourneyFactsAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        Guid userId,
        AccessFixture access,
        SupplyFixture supply,
        Guid subscriptionId,
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM public.users
                    WHERE id = $1 AND status = 'disabled'),
                (SELECT count(*) FROM public.groups
                    WHERE id = $2 AND status = 'active'
                      AND activation_supply_readiness_token IS NOT NULL),
                (SELECT count(*) FROM public.subscription_templates
                    WHERE id = $3 AND status = 'active'),
                (SELECT count(*) FROM public.subscriptions
                    WHERE id = $4 AND status = 'active' AND version = 3),
                (SELECT count(*) FROM public.api_keys
                    WHERE id = $5 AND status = 'revoked' AND version = 2),
                (SELECT count(*) FROM public.audit_logs
                    WHERE target_id IN ($1, $2, $3, $4, $5)
                      AND action IN (
                          'identity.user.created',
                          'identity.user.updated',
                          'groupquota.group.created',
                          'groupquota.group.activated',
                          'subscription_access.template.created',
                          'subscription_access.subscription.assigned',
                          'subscription_access.subscription.updated',
                          'identity.api_key.created',
                          'identity.api_key.revoked'
                      )),
                (SELECT count(*) FROM public.outbox_messages
                    WHERE aggregate_id IN ($1, $2, $3, $4, $5)
                      AND event_type IN (
                          'user_created',
                          'user_updated',
                          'group_created',
                          'group_activated',
                          'template_created',
                          'subscription_assigned',
                          'subscription_updated'
                      )),
                (SELECT count(*) FROM public.idempotency_records),
                (SELECT count(*) FROM public.accounts),
                (SELECT count(*) FROM public.accounts
                    WHERE id = $6
                      AND provider = 'openai_compatible'
                      AND status = 'active'
                      AND last_health_status = 'healthy'
                      AND version = $8
                      AND pg_catalog.strpos(
                          credential_envelope::text, $9) = 0),
                (SELECT count(*) FROM public.channels),
                (SELECT count(*) FROM public.channels
                    WHERE id = $7
                      AND provider = 'openai_compatible'
                      AND status = 'active'
                      AND version = 2),
                (SELECT count(*) FROM public.group_supply_configurations),
                (SELECT count(*) FROM public.group_supply_configurations
                    WHERE group_id = $2
                      AND channel_id = $7
                      AND version = $10),
                (SELECT count(*) FROM public.group_accounts),
                (SELECT count(*) FROM public.group_accounts
                    WHERE group_id = $2
                      AND account_id = $6
                      AND is_enabled),
                (SELECT count(*) FROM public.groups
                    WHERE id = $2
                      AND activation_supply_readiness_token LIKE 'v1.%');
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(access.GroupId);
        command.Parameters.AddWithValue(access.TemplateId);
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(apiKeyId);
        command.Parameters.AddWithValue(supply.AccountId);
        command.Parameters.AddWithValue(supply.ChannelId);
        command.Parameters.AddWithValue(supply.AccountVersion);
        command.Parameters.AddWithValue(UpstreamCredential);
        command.Parameters.AddWithValue(supply.ConfigurationVersion);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        for (int index = 0; index < 5; index++)
        {
            Assert.Equal(1, reader.GetInt64(index));
        }

        Assert.Equal(10, reader.GetInt64(5));
        Assert.Equal(8, reader.GetInt64(6));
        Assert.True(reader.GetInt64(7) >= 9);
        for (int index = 8; index <= 16; index++)
        {
            Assert.Equal(1, reader.GetInt64(index));
        }
    }

    private static async ValueTask<string> LoginAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await environment.Client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email, password },
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return Assert.IsType<string>(
            json.RootElement.GetProperty("access_token").GetString());
    }

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using JsonDocument problem = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
    }

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return JsonDocument.Parse(body);
    }

    private static HttpRequestMessage JsonCommand(
        HttpMethod method,
        string path,
        string accessToken,
        object body,
        string contentType = "application/json",
        string? idempotencyKey = null,
        string? ifMatch = null)
    {
        HttpRequestMessage request = AuthorizedRequest(method, path, accessToken);
        request.Content = JsonContent.Create(body);
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

    private static HttpRequestMessage AuthorizedRequest(
        HttpMethod method,
        string path,
        string accessToken)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private sealed class LoopbackModelsUpstream : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task serveTask;
        private readonly Lock requestsGate = new();
        private readonly List<UpstreamRequest> requests = [];

        internal LoopbackModelsUpstream()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseAddress = $"http://127.0.0.1:{port}";
            serveTask = ServeAsync(shutdown.Token);
        }

        internal string BaseAddress { get; }

        internal ValueTask AssertModelsOnlyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpstreamRequest[] observed;
            lock (requestsGate)
            {
                observed = [.. requests];
            }

            Assert.NotEmpty(observed);
            Assert.All(
                observed,
                static request =>
                {
                    Assert.Equal("GET /models HTTP/1.1", request.RequestLine);
                    Assert.Equal(
                        $"Bearer {UpstreamCredential}",
                        request.Authorization);
                });
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await serveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            catch (SocketException) when (shutdown.IsCancellationRequested)
            {
            }
            finally
            {
                shutdown.Dispose();
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                using TcpClient connection = await listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                await HandleAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        private async ValueTask HandleAsync(
            TcpClient connection,
            CancellationToken cancellationToken)
        {
            using NetworkStream network = connection.GetStream();
            using StreamReader reader = new(
                network,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);
            string requestLine = await reader.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) ?? string.Empty;
            string? authorization = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                   is { Length: > 0 } header)
            {
                if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = header["Authorization:".Length..].Trim();
                }
            }

            lock (requestsGate)
            {
                requests.Add(new UpstreamRequest(requestLine, authorization));
            }

            bool isModelsRequest = string.Equals(
                requestLine,
                "GET /models HTTP/1.1",
                StringComparison.Ordinal);
            byte[] body = Encoding.UTF8.GetBytes(
                isModelsRequest ? """{"data":[]}""" : """{"error":"not_found"}""");
            string status = isModelsRequest
                ? "HTTP/1.1 200 OK"
                : "HTTP/1.1 404 Not Found";
            byte[] headers = Encoding.ASCII.GetBytes(
                $"{status}\r\nContent-Type: application/json\r\n"
                + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await network.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
            await network.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await network.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed record UpstreamRequest(
            string RequestLine,
            string? Authorization);
    }

    private sealed record UserFixture(Guid UserId, string AccessToken);

    private sealed record AccessFixture(Guid GroupId, Guid TemplateId);

    private sealed record SubscriptionFixture(Guid SubscriptionId);

    private sealed record ApiKeyFixture(Guid ApiKeyId);

    private sealed record SupplyFixture(
        Guid AccountId,
        Guid ChannelId,
        long AccountVersion,
        long ConfigurationVersion);

}

#pragma warning restore MA0051
