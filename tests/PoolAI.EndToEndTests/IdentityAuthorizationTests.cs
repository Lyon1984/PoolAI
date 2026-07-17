using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.EndToEndTests;

public sealed class IdentityAuthorizationTests : IAsyncDisposable
{
    private readonly PoolAiApiFactory _factory = new();

    [Fact]
    public async Task ClosedRegistrationIsUnavailable()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new
            {
                email = "person@example.test",
                password = "correct horse battery staple",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminUsersRejectsMissingAuthenticationWithCanonicalProblem()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "authentication_required");
    }

    [Fact]
    public async Task AdminUsersRejectsAuthenticatedUserRoleBeforeCallingTheUseCase()
    {
        using HttpClient client = AuthenticatedClient(
            role: "user",
            tokenVersion: 1);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertProblemAsync(response, "role_required");
    }

    [Fact]
    public async Task AdminUsersRejectsJwtWithoutRequiredIdentityClaims()
    {
        using HttpClient client = AuthenticatedClient(
            role: "admin",
            tokenVersion: null);

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "invalid_user_token");
    }

    [Fact]
    public async Task AdminUsersRejectsEmptyJwtSubjectWithCanonicalUnauthorizedProblem()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                _factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: Guid.Empty));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "invalid_user_token");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("owner")]
    public async Task AdminUsersRejectsMissingOrUnknownJwtRoleAsInvalidToken(string? role)
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                _factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role,
                tokenVersion: 1,
                TimeProvider.System.GetUtcNow().AddMinutes(5)));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "invalid_user_token");
    }

    [Theory]
    [InlineData("GET", "/api/v1/admin/users/00000000-0000-0000-0000-000000000000")]
    [InlineData("PATCH", "/api/v1/admin/users/00000000-0000-0000-0000-000000000000")]
    [InlineData("POST", "/api/v1/admin/users/00000000-0000-0000-0000-000000000000/password-reset")]
    public async Task EmptyRouteIdentifierReturnsCanonicalBadRequest(
        string method,
        string path)
    {
        using HttpClient client = AuthenticatedClient("admin", tokenVersion: 1);
        using HttpRequestMessage request = new(new HttpMethod(method), path);
        if (string.Equals(method, "PATCH", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { display_name = "Updated display name" });
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/merge-patch+json");
            request.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "empty-user-patch");
        }
        else if (string.Equals(method, "POST", StringComparison.Ordinal))
        {
            request.Content = JsonContent.Create(new { reason = "Security recovery" });
            request.Headers.TryAddWithoutValidation("Idempotency-Key", "empty-user-reset");
        }

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_request");
    }

    [Theory]
    [InlineData("wrong-issuer", "PoolAI.Web", -10, false)]
    [InlineData("PoolAI", "wrong-audience", -10, false)]
    [InlineData("PoolAI", "PoolAI.Web", -120, false)]
    [InlineData("PoolAI", "PoolAI.Web", -10, true)]
    public async Task AdminUsersRejectsInvalidBearerTokens(
        string issuer,
        string audience,
        int expiresFromNowSeconds,
        bool useWrongKey)
    {
        byte[] key = useWrongKey
            ? RandomNumberGenerator.GetBytes(32)
            : _factory.JwtSigningKey;
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                key,
                issuer,
                audience,
                "admin",
                tokenVersion: 1,
                TimeProvider.System.GetUtcNow().AddSeconds(expiresFromNowSeconds)));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(value.Scheme, "Bearer", StringComparison.Ordinal));
        await AssertProblemAsync(response, "invalid_user_token");
    }

    [Fact]
    public async Task JwtLifetimeUsesInjectedTimeProviderAndThirtySecondSkew()
    {
        DateTimeOffset now = DateTimeOffset.Parse(
            "2030-01-02T03:04:05Z",
            CultureInfo.InvariantCulture);
        await using SuccessfulIdentityApiFactory factory = new()
        {
            AuthorizationTimeProvider = new FixedTimeProvider(now),
        };

        using HttpClient withinSkew = factory.CreateClient();
        withinSkew.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                now.AddSeconds(-30)));
        using HttpResponseMessage accepted = await withinSkew.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        using HttpClient beyondSkew = factory.CreateClient();
        beyondSkew.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                now.AddSeconds(-31)));
        using HttpResponseMessage rejected = await beyondSkew.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        using HttpClient invertedLifetime = factory.CreateClient();
        invertedLifetime.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                now.AddSeconds(-20),
                notBefore: now.AddSeconds(20)));
        using HttpResponseMessage inverted = await invertedLifetime.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inverted.StatusCode);
        await AssertProblemAsync(rejected, "invalid_user_token");
        await AssertProblemAsync(inverted, "invalid_user_token");
    }

    [Fact]
    public async Task JwtRejectsAValidSignatureUsingAnUnapprovedAlgorithm()
    {
        await using SuccessfulIdentityApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                algorithm: "HS384"));

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemAsync(response, "invalid_user_token");
    }

    [Fact]
    public async Task PasswordResetTokenFailureReturnsExplicitChallenge()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                token = new string('A', 43),
                new_password = "correct horse battery staple",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            response.Headers.WwwAuthenticate,
            static value => string.Equals(
                value.Scheme,
                "PasswordReset",
                StringComparison.Ordinal));
        await AssertProblemAsync(response, "password_reset_token_invalid");
    }

    [Fact]
    public async Task ApplicationPasswordPolicyFailureIncludesFieldErrors()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new
            {
                token = new string('P', 43),
                new_password = "correct horse battery staple",
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response, "password_policy_failed", "/new_password");
    }

    [Fact]
    public async Task ApplicationValidationFailureIncludesFieldErrors()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = CreateUserRequest(
            "validation@example.test",
            "application-validation-error");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response, "validation_failed", "/");
    }

    [Fact]
    public async Task AdminCreateUserCanonicalizesIdnaDomainBeforeCallingApplication()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = CreateUserRequest(
            "Admin+Reset@BÜCHER.Example",
            "idna-mailbox");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument user = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "Admin+Reset@xn--bcher-kva.example",
            user.RootElement.GetProperty("email").GetString());
    }

    [Theory]
    [InlineData("PoolAI <admin@example.test>")]
    [InlineData(" admin@example.test")]
    [InlineData("\"admin\"@example.test")]
    [InlineData("admín@example.test")]
    [InlineData("admin..reset@example.test")]
    [InlineData("admin@[127.0.0.1]")]
    [InlineData("admin@invalid_domain.test")]
    public async Task AdminCreateUserRejectsMailboxTheEmailWorkerCannotDeliver(string email)
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = CreateUserRequest(email, "invalid-mailbox");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response, "validation_failed", "/email");
    }

    [Fact]
    public async Task AdminCreateUserRejectsControlCharacterDisplayNameWithFieldError()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email = "admin@example.test",
                display_name = "Invalid\u0001Name",
                role = "user",
                temporary_password = "correct horse battery staple",
            }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "control-display-name");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response, "validation_failed", "/display_name");
    }

    [Fact]
    public async Task AdminPasswordResetRejectsMultilineReasonWithFieldError()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/v1/admin/users/{Guid.CreateVersion7():D}/password-reset")
        {
            Content = JsonContent.Create(new { reason = "Invalid\nreason" }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "multiline-reason");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await AssertProblemAsync(response, "validation_failed", "/reason");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("not-a-number")]
    public async Task AdminListRejectsInvalidLimitAsBadRequest(string limit)
    {
        using HttpClient client = AuthenticatedClient("admin", tokenVersion: 1);
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/admin/users?limit={Uri.EscapeDataString(limit)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemAsync(response, "invalid_request", "/limit");
    }

    [Fact]
    public async Task VersionConflictReturnsCurrentEntityTag()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "admin",
                tokenVersion: 1,
                TimeProvider.System.GetUtcNow().AddMinutes(5)));
        Guid userId = Guid.CreateVersion7();
        using HttpRequestMessage firstRequest = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new { display_name = "Updated display name" }),
        };
        firstRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        firstRequest.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        firstRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "etag-e2e-test");

        using HttpResponseMessage firstResponse = await client.SendAsync(
            firstRequest,
            TestContext.Current.CancellationToken);
        using HttpRequestMessage replayRequest = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new { display_name = "Updated display name" }),
        };
        replayRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        replayRequest.Headers.TryAddWithoutValidation("If-Match", "\"v1\"");
        replayRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "etag-e2e-test");
        using HttpResponseMessage replayResponse = await client.SendAsync(
            replayRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.PreconditionFailed, firstResponse.StatusCode);
        Assert.Equal(firstResponse.StatusCode, replayResponse.StatusCode);
        Assert.Equal("\"v9\"", firstResponse.Headers.ETag?.Tag);
        Assert.Equal(firstResponse.Headers.ETag, replayResponse.Headers.ETag);
        Assert.NotEqual(
            Assert.Single(firstResponse.Headers.GetValues("X-Request-Id")),
            Assert.Single(replayResponse.Headers.GetValues("X-Request-Id")));
        await AssertProblemAsync(
            firstResponse,
            "version_conflict",
            expectedRetryable: true);
        await AssertProblemAsync(
            replayResponse,
            "version_conflict",
            expectedRetryable: true);
        await AssertEquivalentReplayProblemAsync(firstResponse, replayResponse);
    }

    [Fact]
    public async Task EveryAdminEndpointMapsSuccessfulApplicationResults()
    {
        await using SuccessfulIdentityApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        Guid userId = SuccessfulIdentityUseCases.UserId.Value;

        using HttpResponseMessage list = await client.GetAsync(
            "/api/v1/admin/users?cursor=opaque-cursor&limit=25",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage get = await client.GetAsync(
            $"/api/v1/admin/users/{userId:D}",
            TestContext.Current.CancellationToken);
        using HttpRequestMessage updateRequest = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new
            {
                display_name = "Updated person",
                role = "operator",
                status = "disabled",
                reason = "operator transition",
            }),
        };
        updateRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        updateRequest.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        updateRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "successful-update");
        using HttpResponseMessage update = await client.SendAsync(
            updateRequest,
            TestContext.Current.CancellationToken);
        using HttpRequestMessage resetRequest = new(
            HttpMethod.Post,
            $"/api/v1/admin/users/{userId:D}/password-reset")
        {
            Content = JsonContent.Create(new { reason = "security recovery" }),
        };
        resetRequest.Headers.TryAddWithoutValidation("Idempotency-Key", "successful-reset");
        using HttpResponseMessage reset = await client.SendAsync(
            resetRequest,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("\"v7\"", get.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("\"v8\"", update.Headers.ETag?.Tag);
        Assert.Equal(HttpStatusCode.Accepted, reset.StatusCode);
    }

    [Fact]
    public async Task AdminMutationsRequireIdempotencyAndStrongEntityVersionHeaders()
    {
        await using SuccessfulIdentityApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        Guid userId = SuccessfulIdentityUseCases.UserId.Value;
        using HttpRequestMessage create = CreateUserRequest(
            "person@example.test",
            idempotencyKey: string.Empty);
        using HttpRequestMessage update = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new { display_name = "Updated person" }),
        };
        update.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        update.Headers.TryAddWithoutValidation("Idempotency-Key", "missing-if-match");
        using HttpRequestMessage reset = new(
            HttpMethod.Post,
            $"/api/v1/admin/users/{userId:D}/password-reset")
        {
            Content = JsonContent.Create(new { reason = "security recovery" }),
        };

        using HttpResponseMessage createResponse = await client.SendAsync(
            create,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage updateResponse = await client.SendAsync(
            update,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage resetResponse = await client.SendAsync(
            reset,
            TestContext.Current.CancellationToken);

        Assert.Equal((HttpStatusCode)428, createResponse.StatusCode);
        Assert.Equal((HttpStatusCode)428, updateResponse.StatusCode);
        Assert.Equal((HttpStatusCode)428, resetResponse.StatusCode);
        await AssertProblemAsync(createResponse, "idempotency_key_required");
        await AssertProblemAsync(updateResponse, "if_match_required");
        await AssertProblemAsync(resetResponse, "idempotency_key_required");
    }

    [Fact]
    public async Task UpdateAndResetTransportValidationRejectsEmptyBodiesAndInvalidEtag()
    {
        await using SuccessfulIdentityApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        Guid userId = SuccessfulIdentityUseCases.UserId.Value;
        using HttpRequestMessage emptyUpdate = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new { }),
        };
        emptyUpdate.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        using HttpRequestMessage invalidEtag = new(
            HttpMethod.Patch,
            $"/api/v1/admin/users/{userId:D}")
        {
            Content = JsonContent.Create(new { display_name = "Updated person" }),
        };
        invalidEtag.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/merge-patch+json");
        invalidEtag.Headers.TryAddWithoutValidation("Idempotency-Key", "invalid-etag");
        invalidEtag.Headers.TryAddWithoutValidation("If-Match", "W/\"v7\"");
        using HttpRequestMessage invalidReset = new(
            HttpMethod.Post,
            $"/api/v1/admin/users/{userId:D}/password-reset")
        {
            Content = JsonContent.Create(new { reason = " " }),
        };
        invalidReset.Headers.TryAddWithoutValidation("Idempotency-Key", "blank-reason");

        using HttpResponseMessage emptyUpdateResponse = await client.SendAsync(
            emptyUpdate,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage invalidEtagResponse = await client.SendAsync(
            invalidEtag,
            TestContext.Current.CancellationToken);
        using HttpResponseMessage invalidResetResponse = await client.SendAsync(
            invalidReset,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, emptyUpdateResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEtagResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidResetResponse.StatusCode);
        await AssertProblemAsync(emptyUpdateResponse, "validation_failed", "/");
        await AssertProblemAsync(invalidEtagResponse, "invalid_request");
        await AssertProblemAsync(invalidResetResponse, "validation_failed", "/reason");
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("admin-reset")]
    [InlineData("forgot")]
    [InlineData("complete")]
    public async Task IdentityHandlersRejectJsonCompatibleButWrongContentType(string scenario)
    {
        await using SuccessfulIdentityApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpRequestMessage request = CreateWrongContentTypeRequest(scenario);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await AssertProblemAsync(response, "unsupported_media_type");
    }

    [Fact]
    public async Task AnonymousPasswordResetEndpointsRejectInvalidTransportFields()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage forgot = await client.PostAsJsonAsync(
            "/api/v1/auth/forgot-password",
            new { email = "not-a-mailbox" },
            TestContext.Current.CancellationToken);
        using HttpResponseMessage complete = await client.PostAsJsonAsync(
            "/api/v1/auth/reset-password",
            new { token = "short", new_password = "short" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, forgot.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, complete.StatusCode);
        await AssertProblemAsync(forgot, "validation_failed", "/email");
        await AssertProblemAsync(complete, "validation_failed", "/token");
        await AssertProblemAsync(complete, "validation_failed", "/new_password");
    }

    [Fact]
    public async Task AdminReadsMapApplicationFailuresToCanonicalProblems()
    {
        await using IdentityFailureApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory, "admin", tokenVersion: 1);
        using HttpResponseMessage list = await client.GetAsync(
            "/api/v1/admin/users",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage get = await client.GetAsync(
            $"/api/v1/admin/users/{Guid.CreateVersion7():D}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        await AssertProblemAsync(list, "coordination_unavailable", expectedRetryable: true);
        await AssertProblemAsync(get, "resource_not_found");
    }

    private static HttpRequestMessage CreateWrongContentTypeRequest(string scenario)
    {
        Guid userId = SuccessfulIdentityUseCases.UserId.Value;
        (HttpMethod method, string path, object body, string contentType) = scenario switch
        {
            "create" => (HttpMethod.Post, "/api/v1/admin/users", (object)new
            {
                email = "person@example.test",
                display_name = "Person",
                role = "user",
                temporary_password = "correct horse battery staple",
            }, "application/problem+json"),
            "update" => (HttpMethod.Patch, $"/api/v1/admin/users/{userId:D}",
                (object)new { display_name = "Updated person" }, "application/json"),
            "admin-reset" => (HttpMethod.Post,
                $"/api/v1/admin/users/{userId:D}/password-reset",
                (object)new { reason = "security recovery" }, "application/problem+json"),
            "forgot" => (HttpMethod.Post, "/api/v1/auth/forgot-password",
                (object)new { email = "person@example.test" }, "application/problem+json"),
            "complete" => (HttpMethod.Post, "/api/v1/auth/reset-password", (object)new
            {
                token = new string('A', 43),
                new_password = "correct horse battery staple",
            }, "application/problem+json"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"wrong-content-{scenario}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        return request;
    }

    private HttpClient AuthenticatedClient(string role, long? tokenVersion)
    {
        return AuthenticatedClient(_factory, role, tokenVersion);
    }

    private static HttpClient AuthenticatedClient(
        PoolAiApiFactory factory,
        string? role,
        long? tokenVersion)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                role,
                tokenVersion,
                TimeProvider.System.GetUtcNow().AddMinutes(5)));
        return client;
    }

    internal static string CreateJwt(
        byte[] signingKey,
        string issuer,
        string audience,
        string? role,
        long? tokenVersion,
        DateTimeOffset expiresAt,
        Guid? subjectId = null,
        DateTimeOffset? notBefore = null,
        string algorithm = "HS256")
    {
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = algorithm,
            typ = "JWT",
        }));
        Dictionary<string, object> claims = new(StringComparer.Ordinal)
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["sub"] = (subjectId ?? Guid.CreateVersion7()).ToString(
                "D",
                CultureInfo.InvariantCulture),
            ["sid"] = Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture),
        };
        if (role is not null)
        {
            claims["role"] = role;
        }
        if (tokenVersion is not null)
        {
            claims["token_version"] = tokenVersion.Value;
        }
        if (notBefore is not null)
        {
            claims["nbf"] = notBefore.Value.ToUnixTimeSeconds();
        }

        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        string signingInput = string.Concat(header, ".", payload);
        byte[] signingInputBytes = Encoding.ASCII.GetBytes(signingInput);
        byte[] signature = algorithm switch
        {
            "HS256" => HMACSHA256.HashData(signingKey, signingInputBytes),
            "HS384" => HMACSHA384.HashData(signingKey, signingInputBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };
        return string.Concat(signingInput, ".", Base64Url(signature));
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        string expectedCode,
        string? expectedErrorPointer = null,
        bool expectedRetryable = false)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("X-Request-Id", out IEnumerable<string>? values));
        Assert.True(Guid.TryParse(Assert.Single(values), out _));
        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));
        Assert.Equal(
            Assert.Single(values),
            problem.RootElement.GetProperty("request_id").GetString());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            expectedRetryable,
            problem.RootElement.GetProperty("retryable").GetBoolean());
        if (expectedErrorPointer is not null)
        {
            JsonElement errors = problem.RootElement.GetProperty("errors");
            Assert.True(errors.TryGetProperty(expectedErrorPointer, out JsonElement messages));
            Assert.NotEmpty(messages.EnumerateArray());
        }
    }

    private static async Task AssertEquivalentReplayProblemAsync(
        HttpResponseMessage first,
        HttpResponseMessage replay)
    {
        using JsonDocument firstProblem = JsonDocument.Parse(
            await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));
        using JsonDocument replayProblem = JsonDocument.Parse(
            await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false));
        string[] semanticFields =
            ["type", "title", "status", "detail", "instance", "code", "retryable"];
        foreach (string field in semanticFields)
        {
            Assert.Equal(
                firstProblem.RootElement.GetProperty(field).GetRawText(),
                replayProblem.RootElement.GetProperty(field).GetRawText());
        }
    }

    private static HttpRequestMessage CreateUserRequest(string email, string idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/admin/users")
        {
            Content = JsonContent.Create(new
            {
                email,
                display_name = "Mailbox Boundary",
                role = "user",
                temporary_password = "correct horse battery staple",
            }),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private sealed class IdentityFailureApiFactory : PoolAiApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICompletePasswordResetUseCase>();
                services.AddSingleton<ICompletePasswordResetUseCase>(
                    new InvalidPasswordResetUseCase());
                services.RemoveAll<IUpdateUserUseCase>();
                services.AddSingleton<IUpdateUserUseCase>(new VersionConflictUseCase());
                services.RemoveAll<ICreateUserUseCase>();
                services.AddSingleton<ICreateUserUseCase>(new BoundaryCreateUserUseCase());
                FailureReadUseCases readUseCases = new();
                services.RemoveAll<IListUsersUseCase>();
                services.RemoveAll<IGetUserUseCase>();
                services.AddSingleton<IListUsersUseCase>(readUseCases);
                services.AddSingleton<IGetUserUseCase>(readUseCases);
            });
        }
    }

    private sealed class SuccessfulIdentityApiFactory : PoolAiApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                SuccessfulIdentityUseCases useCases = new();
                services.RemoveAll<IListUsersUseCase>();
                services.RemoveAll<IGetUserUseCase>();
                services.RemoveAll<IUpdateUserUseCase>();
                services.RemoveAll<IRequestAdminPasswordResetUseCase>();
                services.AddSingleton<IListUsersUseCase>(useCases);
                services.AddSingleton<IGetUserUseCase>(useCases);
                services.AddSingleton<IUpdateUserUseCase>(useCases);
                services.AddSingleton<IRequestAdminPasswordResetUseCase>(useCases);
            });
        }
    }

    private sealed class SuccessfulIdentityUseCases :
        IListUsersUseCase,
        IGetUserUseCase,
        IUpdateUserUseCase,
        IRequestAdminPasswordResetUseCase
    {
        internal static readonly EntityId UserId = new(Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634070"));
        private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
            "2026-07-17T00:00:00Z",
            CultureInfo.InvariantCulture);

        public ValueTask<Result<UserPage>> ExecuteAsync(
            ListUsersQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
            new UserPage([View(version: 7)], "next-cursor", HasMore: true)));

        public ValueTask<Result<UserView>> ExecuteAsync(
            GetUserQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
            View(version: 7)));

        public ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
            UpdateUserCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
            new IdentityCommandOutcome<UserView>(
                200,
                IsReplay: false,
                View(version: 8),
                ETag: "\"v8\"")));

        public ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
            AdminPasswordResetCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
            new IdentityCommandOutcome(202, IsReplay: false)));

        private static UserView View(long version) => new(
            UserId,
            "person@example.test",
            "Person",
            SystemRole.User,
            UserLifecycle.Active,
            version,
            Timestamp,
            Timestamp);
    }

    private sealed class InvalidPasswordResetUseCase : ICompletePasswordResetUseCase
    {
        public ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
            CompletePasswordResetCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            command.Token.All(static character => character == 'P')
                ? Result.Failure<IdentityCommandOutcome>(
                    IdentityErrorCodes.PasswordPolicyFailed,
                    "The password does not satisfy policy.")
                : Result.Failure<IdentityCommandOutcome>(
                    IdentityErrorCodes.PasswordResetTokenInvalid,
                    "The password-reset token is invalid or expired."));
    }

    private sealed class BoundaryCreateUserUseCase : ICreateUserUseCase
    {
        public ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
            CreateUserCommand command,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(
                    command.Email,
                    "Admin+Reset@xn--bcher-kva.example",
                    StringComparison.Ordinal))
            {
                return ValueTask.FromResult(
                    Result.Failure<IdentityCommandOutcome<UserView>>(
                        IdentityErrorCodes.ValidationFailed,
                        "The create-user request failed application validation."));
            }

            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            UserView view = new(
                EntityId.New(),
                command.Email,
                command.DisplayName,
                SystemRole.User,
                UserLifecycle.Active,
                1,
                now,
                now);
            return ValueTask.FromResult(Result.Success(
                new IdentityCommandOutcome<UserView>(
                    201,
                    IsReplay: false,
                    view,
                    ETag: "\"v1\"")));
        }
    }

    private sealed class VersionConflictUseCase : IUpdateUserUseCase
    {
        public ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
            UpdateUserCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Result.Failure<IdentityCommandOutcome<UserView>>(
                IdentityErrorCodes.VersionConflict,
                "The user version has changed.",
                etag: "\"v9\"",
                presentation: new ResultErrorPresentation(
                    IdentityErrorCodes.VersionConflict,
                    412,
                    "Version conflict",
                    "The resource version no longer matches; retrieve it again before retrying.",
                    Retryable: true)));
    }

    private sealed class FailureReadUseCases : IListUsersUseCase, IGetUserUseCase
    {
        public ValueTask<Result<UserPage>> ExecuteAsync(
            ListUsersQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Result.Failure<UserPage>(
                IdentityErrorCodes.CoordinationUnavailable,
                "The user directory is temporarily unavailable."));

        public ValueTask<Result<UserView>> ExecuteAsync(
            GetUserQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            Result.Failure<UserView>(
                IdentityErrorCodes.ResourceNotFound,
                "The user does not exist."));
    }
}
