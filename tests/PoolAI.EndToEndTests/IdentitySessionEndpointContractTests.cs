using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.EndToEndTests;

public sealed class IdentitySessionEndpointContractTests
{
    [Fact]
    public async Task SessionEndpointsReturnFrozenSuccessShapesAndHeaders()
    {
        await using SuccessfulSessionApiFactory factory = new();
        using HttpClient authenticated = AuthenticatedClient(factory);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        await AssertProfileAsync(authenticated, cancellationToken).ConfigureAwait(true);
        await AssertPasswordAsync(authenticated, cancellationToken).ConfigureAwait(true);
        await AssertTotpSetupAsync(authenticated, cancellationToken).ConfigureAwait(true);
        await AssertTotpConfirmAsync(authenticated, cancellationToken).ConfigureAwait(true);
        await AssertTotpDisableAsync(authenticated, cancellationToken).ConfigureAwait(true);

        using HttpResponseMessage logout = await authenticated.PostAsync(
            "/api/v1/auth/logout",
            content: null,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using HttpClient anonymous = factory.CreateClient();
        using HttpResponseMessage refresh = await anonymous.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refresh_token = new string('R', 43) },
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        AssertNoStore(refresh);
        using JsonDocument token = JsonDocument.Parse(
            await refresh.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal("Bearer", token.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(900, token.RootElement.GetProperty("expires_in").GetInt32());
        Assert.Equal(2_592_000, token.RootElement.GetProperty("refresh_expires_in").GetInt32());
    }

    [Fact]
    public async Task LogoutRejectsRefreshTokenWithAllSessionsBeforeUseCase()
    {
        await using SuccessfulSessionApiFactory factory = new();
        using HttpClient client = AuthenticatedClient(factory);
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new
            {
                all_sessions = true,
                refresh_token = new string('R', 43),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation_failed", problem.RootElement.GetProperty("code").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("/", out _));
        Assert.Equal(0, factory.UseCases.LogoutCalls);
    }

    private static async ValueTask AssertProfileAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/me",
            cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v7\"", response.Headers.ETag?.Tag);
        AssertNoStore(response);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Assert.True(body.RootElement.GetProperty("totp_enabled").GetBoolean());
        Assert.Equal("user", body.RootElement.GetProperty("role").GetString());
    }

    private static async ValueTask AssertPasswordAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = JsonCommand(
            "/api/v1/me/password",
            new
            {
                current_password = "current-password",
                new_password = "new-password-123",
                reason = "scheduled rotation",
            },
            "password-success",
            includeIfMatch: true);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("\"v8\"", response.Headers.ETag?.Tag);
        AssertNoStore(response);
    }

    private static async ValueTask AssertTotpSetupAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = JsonCommand(
            "/api/v1/me/totp/setup",
            new { current_password = "current-password" },
            "totp-setup-success",
            includeIfMatch: false);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(600, body.RootElement.GetProperty("expires_in").GetInt32());
        Assert.Equal("JBSWY3DPEHPK3PXP", body.RootElement.GetProperty("secret").GetString());
    }

    private static async ValueTask AssertTotpConfirmAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = JsonCommand(
            "/api/v1/me/totp/confirm",
            new { challenge_id = SuccessfulSessionUseCases.ChallengeId, totp_code = "123456" },
            "totp-confirm-success",
            includeIfMatch: true);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v9\"", response.Headers.ETag?.Tag);
        AssertNoStore(response);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(8, body.RootElement.GetProperty("recovery_codes").GetArrayLength());
    }

    private static async ValueTask AssertTotpDisableAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = JsonCommand(
            "/api/v1/me/totp/disable",
            new { current_password = "current-password", totp_code = "123456" },
            "totp-disable-success",
            includeIfMatch: true);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("\"v10\"", response.Headers.ETag?.Tag);
    }

    private static HttpRequestMessage JsonCommand(
        string path,
        object body,
        string idempotencyKey,
        bool includeIfMatch)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (includeIfMatch)
        {
            request.Headers.TryAddWithoutValidation("If-Match", "\"v7\"");
        }

        return request;
    }

    private static HttpClient AuthenticatedClient(PoolAiApiFactory factory)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            IdentityAuthorizationTests.CreateJwt(
                factory.JwtSigningKey,
                "PoolAI",
                "PoolAI.Web",
                "user",
                tokenVersion: 7,
                TimeProvider.System.GetUtcNow().AddMinutes(5),
                subjectId: SuccessfulSessionUseCases.UserId));
        return client;
    }

    private static void AssertNoStore(HttpResponseMessage response) => Assert.Contains(
        "no-store",
        response.Headers.CacheControl?.ToString(),
        StringComparison.Ordinal);

    private sealed class SuccessfulSessionApiFactory : PoolAiApiFactory
    {
        internal SuccessfulSessionUseCases UseCases { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRefreshSessionUseCase>();
                services.RemoveAll<ILogoutUseCase>();
                services.RemoveAll<IGetCurrentUserUseCase>();
                services.RemoveAll<IChangePasswordUseCase>();
                services.RemoveAll<ISetupTotpUseCase>();
                services.RemoveAll<IConfirmTotpUseCase>();
                services.RemoveAll<IDisableTotpUseCase>();
                services.AddSingleton<IRefreshSessionUseCase>(UseCases);
                services.AddSingleton<ILogoutUseCase>(UseCases);
                services.AddSingleton<IGetCurrentUserUseCase>(UseCases);
                services.AddSingleton<IChangePasswordUseCase>(UseCases);
                services.AddSingleton<ISetupTotpUseCase>(UseCases);
                services.AddSingleton<IConfirmTotpUseCase>(UseCases);
                services.AddSingleton<IDisableTotpUseCase>(UseCases);
            });
        }
    }

    private sealed class SuccessfulSessionUseCases :
        IRefreshSessionUseCase,
        ILogoutUseCase,
        IGetCurrentUserUseCase,
        IChangePasswordUseCase,
        ISetupTotpUseCase,
        IConfirmTotpUseCase,
        IDisableTotpUseCase
    {
        internal static readonly Guid UserId = Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634071");
        internal static readonly Guid ChallengeId = Guid.Parse(
            "019bd5e8-30e0-7d4c-a7f2-bb1db0634072");
        private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse(
            "2026-07-17T00:00:00Z",
            CultureInfo.InvariantCulture);

        internal int LogoutCalls { get; private set; }

        public ValueTask<Result<TokenPairView>> ExecuteAsync(
            RefreshSessionCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new TokenPairView("access-token", "refresh-token", 900, 2_592_000)));

        public ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
            LogoutCommand command,
            CancellationToken cancellationToken)
        {
            LogoutCalls++;
            return ValueTask.FromResult(Result.Success(new IdentityCommandOutcome(204, false)));
        }

        public ValueTask<Result<CurrentUserView>> ExecuteAsync(
            GetCurrentUserQuery query,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new CurrentUserView(
                    new EntityId(UserId),
                    "person@example.test",
                    "Person",
                    SystemRole.User,
                    UserLifecycle.Active,
                    TotpEnabled: true,
                    Timestamp,
                    Version: 7,
                    Timestamp,
                    Timestamp)));

        public ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
            ChangePasswordCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new IdentityCommandOutcome(204, false, "\"v8\"")));

        public ValueTask<Result<IdentityCommandOutcome<TotpSetupView>>> ExecuteAsync(
            SetupTotpCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new IdentityCommandOutcome<TotpSetupView>(
                    200,
                    false,
                    new TotpSetupView(
                        new EntityId(ChallengeId),
                        "JBSWY3DPEHPK3PXP",
                        "otpauth://totp/PoolAI:person%40example.test?secret=JBSWY3DPEHPK3PXP&issuer=PoolAI",
                        600))));

        public ValueTask<Result<IdentityCommandOutcome<TotpConfirmView>>> ExecuteAsync(
            ConfirmTotpCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new IdentityCommandOutcome<TotpConfirmView>(
                    200,
                    false,
                    new TotpConfirmView(Enumerable.Range(1, 8)
                        .Select(static number => $"RECOVERY-{number:D2}")
                        .ToArray()),
                    "\"v9\"")));

        public ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
            DisableTotpCommand command,
            CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success(
                new IdentityCommandOutcome(204, false, "\"v10\"")));
    }
}
