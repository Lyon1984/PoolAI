using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Application;
using PoolAI.Modules.Operations.Endpoints;

namespace PoolAI.EndToEndTests;

public sealed class OutboxReplayEndpointTests
{
    private static readonly EntityId ActorId = new(Guid.Parse(
        "01900000-0000-7000-8000-00000000e451"));
    private static readonly EntityId SourceMessageId = new(Guid.Parse(
        "01900000-0000-7000-8000-00000000e452"));
    private static readonly EntityId ReplacementMessageId = new(Guid.Parse(
        "01900000-0000-7000-8000-00000000e453"));

    [Fact]
    public async Task AdminReplayCreatesAVisibleAuditedReplacementEvent()
    {
        await using OutboxReplayTestHost factory =
            await OutboxReplayTestHost.CreateAsync().ConfigureAwait(true);
        factory.UseCase.ConfiguredResult = Result.Success(new OutboxReplayOutcome(
            IsReplay: false,
            ReplacementMessageId,
            EventSequence: 9223372036854775000,
            SourceMessageId));
        using HttpClient client = factory.CreateClient("admin");
        using HttpRequestMessage request = ReplayRequest(
            "m3-e4-end-to-end",
            "poison message remediated; replay approved");
        request.Headers.UserAgent.ParseAdd("PoolAI-M3E4-E2E/1.0");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues(
            "X-Request-Id",
            out IEnumerable<string>? requestIds));
        string requestId = Assert.Single(requestIds);
        Assert.True(Guid.TryParse(requestId, out Guid parsedRequestId));
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(true);
        JsonElement receipt = document.RootElement;
        Assert.Equal(ReplacementMessageId.Value, receipt.GetProperty("message_id").GetGuid());
        Assert.Equal(
            "9223372036854775000",
            receipt.GetProperty("event_sequence").GetString());
        Assert.Equal(SourceMessageId.Value, receipt.GetProperty("replay_of").GetGuid());
        Assert.Equal("pending", receipt.GetProperty("status").GetString());
        Assert.Equal(4, receipt.EnumerateObject().Count());
        Assert.False(receipt.TryGetProperty("payload", out _));
        Assert.False(receipt.TryGetProperty("last_error", out _));

        ReplayDeadOutboxCommand command = Assert.IsType<ReplayDeadOutboxCommand>(
            factory.UseCase.LastCommand);
        Assert.Equal(ActorId, command.Actor.UserId);
        Assert.Equal(parsedRequestId, command.RequestId.Value);
        Assert.Equal(OperationsControlRole.Admin, command.Actor.Role);
        Assert.Equal(7, command.Actor.TokenVersion);
        Assert.Equal(SourceMessageId, command.SourceMessageId);
        Assert.Equal("m3-e4-end-to-end", command.IdempotencyKey);
        Assert.Equal("poison message remediated; replay approved", command.Reason);
        Assert.StartsWith("sha256:", command.UserAgent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-key", 428, "idempotency_key_required", null)]
    [InlineData("extra-property", 422, "validation_failed", "/unexpected")]
    [InlineData("blank-reason", 422, "validation_failed", "/reason")]
    [InlineData("nul-reason", 422, "validation_failed", "/reason")]
    [InlineData("c1-reason", 422, "validation_failed", "/reason")]
    [InlineData("wrong-content-type", 415, "unsupported_media_type", null)]
    public async Task InvalidReplayRequestsFailBeforeTheUseCase(
        string scenario,
        int expectedStatus,
        string expectedCode,
        string? expectedPointer)
    {
        await using OutboxReplayTestHost factory =
            await OutboxReplayTestHost.CreateAsync().ConfigureAwait(true);
        using HttpClient client = factory.CreateClient("admin");
        using HttpRequestMessage request = scenario switch
        {
            "missing-key" => ReplayRequest(null, "valid reason"),
            "extra-property" => RawReplayRequest(
                "replay-extra",
                """{"reason":"valid reason","unexpected":true}"""),
            "blank-reason" => ReplayRequest("replay-blank", " "),
            "nul-reason" => RawReplayRequest(
                "replay-nul",
                """{"reason":"blocked\u0000reason"}"""),
            "c1-reason" => RawReplayRequest(
                "replay-c1",
                """{"reason":"blocked\u0085reason"}"""),
            "wrong-content-type" => RawReplayRequest(
                "replay-content",
                "reason=valid",
                "text/plain"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(
            response,
            (HttpStatusCode)expectedStatus,
            expectedCode,
            expectedPointer).ConfigureAwait(true);
        Assert.Equal(0, factory.UseCase.Calls);
    }

    [Fact]
    public async Task ValidUnicodeReplayReasonIsTrimmedBeforeTheUseCase()
    {
        await using OutboxReplayTestHost factory =
            await OutboxReplayTestHost.CreateAsync().ConfigureAwait(true);
        factory.UseCase.ConfiguredResult = Result.Success(new OutboxReplayOutcome(
            IsReplay: false,
            ReplacementMessageId,
            EventSequence: 904,
            SourceMessageId));
        using HttpClient client = factory.CreateClient("admin");
        using HttpRequestMessage request = ReplayRequest(
            "m3-e4-unicode-reason",
            "\u00A0修复 🔧\u3000");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        ReplayDeadOutboxCommand command = Assert.IsType<ReplayDeadOutboxCommand>(
            factory.UseCase.LastCommand);
        Assert.Equal("修复 🔧", command.Reason);
    }

    [Fact]
    public async Task ReplayRouteRequiresAdminRole()
    {
        await using OutboxReplayTestHost factory =
            await OutboxReplayTestHost.CreateAsync().ConfigureAwait(true);
        using HttpClient client = factory.CreateClient("operator");
        using HttpRequestMessage request = ReplayRequest(
            "m3-e4-operator-forbidden",
            "operator may not replay");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, factory.UseCase.Calls);
    }

    [Theory]
    [InlineData("resource_not_found", 404)]
    [InlineData("resource_conflict", 409)]
    [InlineData("idempotency_conflict", 409)]
    [InlineData("service_unavailable", 503)]
    public async Task ReplayApplicationErrorsUseFrozenControlPlaneProblems(
        string code,
        int expectedStatus)
    {
        await using OutboxReplayTestHost factory =
            await OutboxReplayTestHost.CreateAsync().ConfigureAwait(true);
        factory.UseCase.ConfiguredResult = Result.Failure<OutboxReplayOutcome>(
            code,
            "test failure",
            string.Equals(code, "service_unavailable", StringComparison.Ordinal) ? 1 : null);
        using HttpClient client = factory.CreateClient("admin");
        using HttpRequestMessage request = ReplayRequest(
            $"m3-e4-{code}",
            "verify error mapping");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        await AssertProblemAsync(
            response,
            (HttpStatusCode)expectedStatus,
            code).ConfigureAwait(true);
        Assert.Equal(1, factory.UseCase.Calls);
        if (expectedStatus == 503)
        {
            Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
        }
    }

    private static HttpRequestMessage ReplayRequest(string? key, string reason)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ReplayPath())
        {
            Content = JsonContent.Create(new { reason }),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        return request;
    }

    private static HttpRequestMessage RawReplayRequest(
        string key,
        string body,
        string contentType = "application/json")
    {
        HttpRequestMessage request = new(HttpMethod.Post, ReplayPath())
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return request;
    }

    private static string ReplayPath() =>
        $"/api/v1/admin/outbox-messages/{SourceMessageId.Value:D}/replay";

    private static async ValueTask<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response) => JsonDocument.Parse(
        await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken).ConfigureAwait(false));

    private static async ValueTask AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code,
        string? pointer = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = await ReadJsonAsync(response).ConfigureAwait(false);
        JsonElement problem = document.RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(problem.GetProperty("request_id").GetString(), out _));
        if (pointer is not null)
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(pointer, out _));
        }
    }

    private sealed class OutboxReplayTestHost : IAsyncDisposable
    {
        private const string AuthenticationScheme = "M3E4-Test";
        private readonly IHost _host;

        private OutboxReplayTestHost(IHost host, FakeOutboxReplayUseCase useCase)
        {
            _host = host;
            UseCase = useCase;
        }

        internal FakeOutboxReplayUseCase UseCase { get; }

        internal static async ValueTask<OutboxReplayTestHost> CreateAsync()
        {
            FakeOutboxReplayUseCase useCase = new();
            IHost host = await new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services
                            .AddAuthentication(AuthenticationScheme)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                                AuthenticationScheme,
                                _ => { });
                        services.AddAuthorization();
                        services.AddSingleton<IReplayDeadOutboxUseCase>(useCase);
                    })
                    .Configure(app =>
                    {
                        app.Use(static async (context, next) =>
                        {
                            string requestId = Guid.CreateVersion7().ToString("D");
                            context.TraceIdentifier = requestId;
                            context.Response.Headers["X-Request-Id"] = requestId;
                            await next(context).ConfigureAwait(false);
                        });
                        app.UseRouting();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(static endpoints =>
                            endpoints.MapOutboxReplayEndpoints());
                    }))
                .StartAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            return new OutboxReplayTestHost(host, useCase);
        }

        internal HttpClient CreateClient(string role)
        {
            HttpClient client = _host.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                AuthenticationScheme,
                "authenticated");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Test-Role", role);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _host.Dispose();
            }
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            Claim[] claims =
            [
                new("sub", ActorId.Value.ToString("D")),
                new("role", role),
                new(ClaimTypes.Role, role),
                new("token_version", "7"),
            ];
            ClaimsIdentity identity = new(claims, Scheme.Name);
            AuthenticationTicket ticket = new(
                new ClaimsPrincipal(identity),
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class FakeOutboxReplayUseCase : IReplayDeadOutboxUseCase
    {
        internal int Calls { get; private set; }

        internal ReplayDeadOutboxCommand? LastCommand { get; private set; }

        internal Result<OutboxReplayOutcome> ConfiguredResult { get; set; } = Result.Success(
            new OutboxReplayOutcome(
                IsReplay: false,
                ReplacementMessageId,
                EventSequence: 1,
                SourceMessageId));

        public ValueTask<Result<OutboxReplayOutcome>> ExecuteAsync(
            ReplayDeadOutboxCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastCommand = command;
            return ValueTask.FromResult(ConfiguredResult);
        }
    }
}
