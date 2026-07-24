using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

#pragma warning disable MA0051 // M1 Exit keeps each complete public HTTP proof visible.

namespace PoolAI.EndToEndTests;

public sealed class M1ExitPublicApiEndToEndTests
{
    private const string UserPassword = "M1-Exit-User-Password-123!";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task M1ExitUsesPublicHttpProductionPortsAndRealDependenciesWithoutUpstream()
    {
        // Governing contract: the M1 exit gate permits only a database-backed
        // test Supply-readiness adapter. Every M1-owned fact and port below is
        // exercised through the production HTTP Composition Root.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using NoUpstreamConnectionSentinel upstreamSentinel = new();
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateM1ExitAsync(
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
        await ProvisionM2OwnedSupplyFactsAsync(
            environment,
            access.GroupId,
            upstreamSentinel.BaseAddress,
            cancellationToken).ConfigureAwait(true);
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
            subscription.SubscriptionId,
            apiKey.ApiKeyId,
            cancellationToken).ConfigureAwait(true);
        await upstreamSentinel.AssertNoConnectionsAsync(cancellationToken)
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
        string email = $"m1-exit-{suffix}@poolai.test";
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/admin/users",
            adminToken,
            new
            {
                email,
                display_name = "M1 Exit User",
                role = "user",
                temporary_password = UserPassword,
            },
            idempotencyKey: "m1-exit-user-create");
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
                name = $"M1 Exit {Guid.NewGuid():N}",
                description = "M1 public API acceptance",
                total_tokens = 1_000_000,
            },
            idempotencyKey: "m1-exit-group-create");
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
                name = "M1 Exit Access",
                default_duration_days = 30,
            },
            idempotencyKey: "m1-exit-template-create");
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
            idempotencyKey: "m1-exit-disabled-assignment");
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
            idempotencyKey: "m1-exit-activation-not-ready",
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

    private static async ValueTask ProvisionM2OwnedSupplyFactsAsync(
        PasswordResetHttpEndToEndEnvironment environment,
        Guid groupId,
        string upstreamBaseUrl,
        CancellationToken cancellationToken)
    {
        Guid channelId = Guid.CreateVersion7();
        Guid accountId = Guid.CreateVersion7();
        using NpgsqlConnection connection = await environment.AdministratorDataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using NpgsqlTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        using (NpgsqlCommand channel = new("""
            INSERT INTO public.channels (
                id, provider, name, model_rules, status
            ) VALUES (
                $1, 'openai_compatible', 'M1 Exit channel',
                pg_catalog.jsonb_build_object('gpt-test', true), 'active'
            );
            """, connection, transaction))
        {
            channel.Parameters.AddWithValue(channelId);
            Assert.Equal(
                1,
                await channel.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand account = new("""
            INSERT INTO public.accounts (
                id, provider, name, auth_type, upstream_base_url,
                credential_envelope, credential_prefix, status, last_health_status
            ) VALUES (
                $1, 'openai_compatible', 'M1 Exit account', 'api_key',
                $2,
                pg_catalog.jsonb_build_object('kind', 'noncredential-test-fixture'),
                'fixture', 'active', 'healthy'
            );
            """, connection, transaction))
        {
            account.Parameters.AddWithValue(accountId);
            account.Parameters.AddWithValue(upstreamBaseUrl);
            Assert.Equal(
                1,
                await account.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        using (NpgsqlCommand configuration = new("""
            INSERT INTO public.group_supply_configurations (group_id, channel_id)
            VALUES ($1, $2);
            """, connection, transaction))
        {
            configuration.Parameters.AddWithValue(groupId);
            configuration.Parameters.AddWithValue(channelId);
            Assert.Equal(
                1,
                await configuration.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false));
        }

        using (NpgsqlCommand binding = new("""
            INSERT INTO public.group_accounts (group_id, account_id, is_enabled)
            VALUES ($1, $2, true);
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue(groupId);
            binding.Parameters.AddWithValue(accountId);
            Assert.Equal(
                1,
                await binding.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            idempotencyKey: "m1-exit-activation-ready",
            ifMatch: "\"v1\"");
        using HttpResponseMessage response = await environment.Client.SendAsync(
            activate,
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v2\"", response.Headers.ETag?.Tag);
        using JsonDocument activated = await ReadJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal("active", activated.RootElement.GetProperty("status").GetString());
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
                reason = "M1 access approval",
            },
            idempotencyKey: "m1-exit-subscription-assign");
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
            name = "M1 Exit key",
            group_id = groupId,
            allowed_cidrs = Array.Empty<string>(),
        };
        using HttpRequestMessage create = JsonCommand(
            HttpMethod.Post,
            "/api/v1/me/api-keys",
            user.AccessToken,
            body,
            idempotencyKey: "m1-exit-api-key-create");
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
            idempotencyKey: "m1-exit-api-key-create");
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
            "M1 suspension proof",
            "m1-exit-subscription-suspend",
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
            idempotencyKey: "m1-exit-key-while-suspended");
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
            "M1 restoration proof",
            "m1-exit-subscription-resume",
            "\"v3\"",
            cancellationToken).ConfigureAwait(false);

        using HttpRequestMessage revoke = AuthorizedRequest(
            HttpMethod.Delete,
            $"/api/v1/me/api-keys/{apiKey.ApiKeyId:D}",
            user.AccessToken);
        revoke.Headers.TryAddWithoutValidation("Idempotency-Key", "m1-exit-api-key-revoke");
        revoke.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        revoke.Headers.TryAddWithoutValidation("X-Change-Reason", "M1 revocation proof");
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
            new { status = "disabled", reason = "M1 canonical revocation proof" },
            contentType: "application/merge-patch+json",
            idempotencyKey: "m1-exit-user-disable",
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
                (SELECT count(*) FROM public.idempotency_records);
            """);
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(access.GroupId);
        command.Parameters.AddWithValue(access.TemplateId);
        command.Parameters.AddWithValue(subscriptionId);
        command.Parameters.AddWithValue(apiKeyId);
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

    private sealed class NoUpstreamConnectionSentinel : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task observeTask;
        private int acceptedConnections;

        internal NoUpstreamConnectionSentinel()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseAddress = $"http://127.0.0.1:{port}";
            observeTask = ObserveConnectionsAsync(shutdown.Token);
        }

        internal string BaseAddress { get; }

        internal async ValueTask AssertNoConnectionsAsync(
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
            Assert.Equal(0, Volatile.Read(ref acceptedConnections));
            Assert.False(
                listener.Pending(),
                "The M1 Exit journey queued an unexpected upstream connection.");
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try
            {
                await observeTask.ConfigureAwait(false);
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

        private async Task ObserveConnectionsAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                using TcpClient connection = await listener
                    .AcceptTcpClientAsync(cancellationToken)
                    .ConfigureAwait(false);
                _ = Interlocked.Increment(ref acceptedConnections);
            }
        }
    }

    private sealed record UserFixture(Guid UserId, string AccessToken);

    private sealed record AccessFixture(Guid GroupId, Guid TemplateId);

    private sealed record SubscriptionFixture(Guid SubscriptionId);

    private sealed record ApiKeyFixture(Guid ApiKeyId);

}

#pragma warning restore MA0051
