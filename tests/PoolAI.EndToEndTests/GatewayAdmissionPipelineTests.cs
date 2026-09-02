extern alias PoolAiApi;

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.EndToEndTests;

// Governing contracts: DEC D-029, execution specification 4.8/7.2, and AC-043.
public sealed class GatewayAdmissionPipelineTests
{
    [Theory]
    [InlineData("/v1/models", GatewayAdmissionKind.NonStream)]
    [InlineData("/v1/usage", GatewayAdmissionKind.Usage)]
    public async Task RoutedDataQueriesUseGatewayProblemProjection(
        string path,
        GatewayAdmissionKind kind)
    {
        using GatewayAdmissionController admission = SmallAdmissionController();
        Result<GatewayAdmissionLease> held = await admission.AcquireAsync(
            kind,
            TestContext.Current.CancellationToken);
        int nextCalls = 0;
        PoolAiApi::PoolAI.Api.GatewayAdmissionMiddleware admissionMiddleware = new(
            _ =>
            {
                nextCalls++;
                return Task.CompletedTask;
            },
            admission,
            TimeProvider.System);
        PoolAiApi::PoolAI.Api.RequestIdMiddleware requestIdMiddleware = new(
            admissionMiddleware.InvokeAsync);
        DefaultHttpContext context = CreateRoutedContext(path);

        try
        {
            await requestIdMiddleware.InvokeAsync(context);
            context.Response.Body.Position = 0;
            using JsonDocument problem = await JsonDocument.ParseAsync(
                context.Response.Body,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
            Assert.Equal("application/json", context.Response.ContentType);
            Assert.Equal("1", context.Response.Headers.RetryAfter);
            Assert.Equal(
                "gateway_overloaded",
                problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "gateway_overloaded",
                problem.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(0, nextCalls);
        }
        finally
        {
            await held.Value.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SaturatedControlPartitionRejectsBeforeAuthenticationAndUseCase()
    {
        await using AdmissionApiFactory factory = new(controlQueueLimit: 0);
        GatewayAdmissionController admission = factory.Services
            .GetRequiredService<GatewayAdmissionController>();
        Result<GatewayAdmissionLease> held = await admission.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);

        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/admin/users/",
                TestContext.Current.CancellationToken);
            string body = await response.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            using JsonDocument problem = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal(
                "gateway_overloaded",
                problem.RootElement.GetProperty("code").GetString());
            Assert.False(problem.RootElement.TryGetProperty("error", out _));
            Assert.Equal(
                response.Headers.GetValues("X-Request-Id").Single(),
                problem.RootElement.GetProperty("request_id").GetString());
            Assert.Equal(0, factory.Authentication.AuthenticateCalls);
            Assert.Equal(0, factory.ListUsers.Calls);
        }
        finally
        {
            await held.Value.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task RealInFlightControlRequestRejectsBeforeAuthAndBodyBinding()
    {
        await using AdmissionApiFactory factory = new(controlQueueLimit: 0);
        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> inFlight = client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { email = "admission@example.test", password = "not-a-secret" },
            TestContext.Current.CancellationToken);
        await factory.Login.Entered.WaitAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        int authenticationCallsBeforeRejection = factory.Authentication.AuthenticateCalls;

        try
        {
            using StringContent malformedBody = new(
                "{",
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage rejected = await client.PostAsync(
                "/api/v1/auth/login",
                malformedBody,
                TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            string body = await rejected.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Contains("\"code\":\"gateway_overloaded\"", body, StringComparison.Ordinal);
            Assert.Equal(authenticationCallsBeforeRejection, factory.Authentication.AuthenticateCalls);
            Assert.Equal(1, factory.Login.Calls);
        }
        finally
        {
            factory.Login.Release();
            using HttpResponseMessage _ = await inFlight
                .WaitAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ControlQueueServerBudgetExpiresAsOverloadBeforeAuthentication()
    {
        await using AdmissionApiFactory factory = new(controlQueueLimit: 1);
        GatewayAdmissionController admission = factory.Services
            .GetRequiredService<GatewayAdmissionController>();
        Result<GatewayAdmissionLease> held = await admission.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);

        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync(
                "/api/v1/admin/users/",
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal(0, factory.Authentication.AuthenticateCalls);
            Assert.Equal(0, factory.ListUsers.Calls);
        }
        finally
        {
            await held.Value.DisposeAsync().ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task ClientAbortCancelsQueueWithoutWritingAnOverloadOrLosingCapacity()
    {
        await using AdmissionApiFactory factory = new(controlQueueLimit: 1);
        GatewayAdmissionController admission = factory.Services
            .GetRequiredService<GatewayAdmissionController>();
        Result<GatewayAdmissionLease> held = await admission.AcquireAsync(
            GatewayAdmissionKind.Control,
            TestContext.Current.CancellationToken);
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource clientAbort = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await client.GetAsync("/api/v1/admin/users/", clientAbort.Token)
                    .ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.Equal(0, factory.Authentication.AuthenticateCalls);
        Assert.Equal(0, factory.ListUsers.Calls);

        await held.Value.DisposeAsync().ConfigureAwait(true);
        using HttpResponseMessage subsequent = await client.GetAsync(
                "/api/v1/admin/users/",
                TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, subsequent.StatusCode);
        Assert.True(factory.Authentication.AuthenticateCalls > 0);
        Assert.Equal(0, factory.ListUsers.Calls);
    }

    [Theory]
    [InlineData("/api/v1/not-a-route", GatewayAdmissionKind.Control)]
    [InlineData("/v1/models", GatewayAdmissionKind.NonStream)]
    [InlineData("/v1/usage", GatewayAdmissionKind.Usage)]
    [InlineData("/v1/chat/completions", GatewayAdmissionKind.Sse)]
    public async Task UnmappedPathsAreNotTurnedIntoSyntheticOverloads(
        string path,
        GatewayAdmissionKind saturatedKind)
    {
        await using AdmissionApiFactory factory = new(controlQueueLimit: 0);
        GatewayAdmissionController admission = factory.Services
            .GetRequiredService<GatewayAdmissionController>();
        Result<GatewayAdmissionLease> held = await admission.AcquireAsync(
            saturatedKind,
            TestContext.Current.CancellationToken);

        try
        {
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync(
                path,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            await held.Value.DisposeAsync().ConfigureAwait(true);
        }
    }

    private sealed class AdmissionApiFactory(int controlQueueLimit) : PoolAiApiFactory
    {
        internal RecordingAuthenticationService Authentication { get; } = new();

        internal RecordingListUsersUseCase ListUsers { get; } = new();

        internal BlockingLoginUseCase Login { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Dictionary<string, string?> admissionConfiguration = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Admission:ControlPermits"] = "1",
                ["Admission:ControlQueueLimit"] = controlQueueLimit.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            };
            foreach ((string key, string? value) in admissionConfiguration)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(admissionConfiguration));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAuthenticationService>();
                services.AddSingleton<IAuthenticationService>(Authentication);
                services.RemoveAll<IListUsersUseCase>();
                services.AddSingleton<IListUsersUseCase>(ListUsers);
                services.RemoveAll<ILoginUseCase>();
                services.AddSingleton<ILoginUseCase>(Login);
            });
        }
    }

    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        private int _authenticateCalls;

        public int AuthenticateCalls => Volatile.Read(ref _authenticateCalls);

        public Task<AuthenticateResult> AuthenticateAsync(
            HttpContext context,
            string? scheme)
        {
            Interlocked.Increment(ref _authenticateCalls);
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(
            HttpContext context,
            string? scheme,
            AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class RecordingListUsersUseCase : IListUsersUseCase
    {
        public int Calls { get; private set; }

        public ValueTask<Result<UserPage>> ExecuteAsync(
            ListUsersQuery query,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromException<Result<UserPage>>(
                new InvalidOperationException("The admission test use case must not execute."));
        }
    }

    private sealed class BlockingLoginUseCase : ILoginUseCase
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        internal Task Entered => _entered.Task;

        internal int Calls => Volatile.Read(ref _calls);

        internal void Release() => _release.TrySetResult();

        public async ValueTask<Result<LoginResultView>> ExecuteAsync(
            LoginCommand command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
            return Result.Failure<LoginResultView>(
                "invalid_credentials",
                "The supplied credentials are invalid.");
        }
    }

    private static GatewayAdmissionController SmallAdmissionController() =>
        new(new GatewayAdmissionOptions(
            dataNonStreamPermits: 1,
            dataStreamPermits: 1,
            controlPermits: 1,
            controlQueueLimit: 0,
            usagePermits: 1,
            usageQueueLimit: 0));

    private static DefaultHttpContext CreateRoutedContext(string path)
    {
        DefaultHttpContext context = new();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(path),
            order: 0,
            new EndpointMetadataCollection(new HttpMethodMetadata([HttpMethods.Get])),
            displayName: "admission-projection-test"));
        return context;
    }
}
