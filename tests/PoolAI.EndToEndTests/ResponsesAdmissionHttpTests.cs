using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.EndToEndTests;

public sealed class ResponsesAdmissionHttpTests
{
    [Theory]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"stream\":true}", GatewayAdmissionKind.Sse)]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"stream\":false}", GatewayAdmissionKind.NonStream)]
    [InlineData("{\"model\":\"test\",\"input\":\"x\"}", GatewayAdmissionKind.NonStream)]
    [InlineData("{\"stream\":true,\"stream\":false}", GatewayAdmissionKind.Sse)]
    [InlineData("{\"stream\":0}", GatewayAdmissionKind.NonStream)]
    [InlineData("{", GatewayAdmissionKind.NonStream)]
    public async Task ExactSelectedPartitionRejectsBeforeAuthenticationAndBodyErrors(string body, GatewayAdmissionKind kind)
    {
        await using ResponsesAdmissionApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var admission = factory.Services.GetRequiredService<GatewayAdmissionController>();
        using GatewayAdmissionLease held = (await admission.AcquireAsync(kind, TestContext.Current.CancellationToken)).Value;
        using HttpResponseMessage response = await SendAsync(client, body, "application/json", authenticated: false);
        await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal(0, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        GatewayAdmissionKind other = kind == GatewayAdmissionKind.Sse ? GatewayAdmissionKind.NonStream : GatewayAdmissionKind.Sse;
        using GatewayAdmissionLease independent = (await admission.AcquireAsync(other, TestContext.Current.CancellationToken)).Value;
    }

    [Theory]
    [InlineData("{", "text/plain", false, 401, "invalid_api_key")]
    [InlineData("{", "application/json", true, 400, "invalid_request")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"stream\":null}", "application/json", true, 422, "validation_failed")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\",\"stream\":true,\"stream\":false}", "application/json", true, 400, "invalid_request")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\"}", "application/json; charset=utf-16", true, 415, "unsupported_media_type")]
    [InlineData("{\"model\":\"test\",\"input\":\"x\"}", "application/problem+json", true, 415, "unsupported_media_type")]
    public async Task AuthenticationPrecedesAuthoritativeMediaSyntaxAndSchemaErrors(string body, string mediaType,
        bool validKey, int status, string code)
    {
        await using ResponsesAdmissionApiFactory factory = new();
        factory.Keys.Valid = validKey;
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await SendAsync(client, body, mediaType, true);
        await AssertProblemAsync(response, (HttpStatusCode)status, code);
        Assert.Equal(1, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        await AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task CompleteGuardSaturationDoesNotReadAuthOrConsumeDataLeases()
    {
        await using ResponsesAdmissionApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var discriminator = factory.Services.GetRequiredService<GatewayModelDiscriminator>();
        List<GatewayModelDiscriminatorLease> guards = [];
        try
        {
            for (int index = 0; index < 8; index++) { guards.Add(discriminator.TryAcquire(TestContext.Current.CancellationToken).Value); }
            using HttpResponseMessage rejected = await SendAsync(client, "{", "text/plain", false);
            await AssertProblemAsync(rejected, HttpStatusCode.TooManyRequests, "gateway_overloaded");
            Assert.Equal(0, factory.Keys.Calls);
            Assert.Equal(0, factory.Users.Calls);
            var admission = factory.Services.GetRequiredService<GatewayAdmissionController>();
            foreach (GatewayAdmissionKind kind in Enum.GetValues<GatewayAdmissionKind>())
            {
                using GatewayAdmissionLease independent = (await admission.AcquireAsync(kind, TestContext.Current.CancellationToken)).Value;
            }
        }
        finally { foreach (var guard in guards) { guard.Dispose(); } }
        await AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task WholePreparationDeadlineFencesLateAuthenticationAndRestoresEveryPermit()
    {
        FakeTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        await using ResponsesAdmissionApiFactory factory = new() { AuthorizationTimeProvider = clock };
        factory.Keys.Block = true;
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> pending = SendAsync(client, "{\"model\":\"test\",\"input\":\"x\",\"stream\":true}", "application/json", true).AsTask();
        await factory.Keys.Entered.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await pending.WaitAsync(TestContext.Current.CancellationToken);
            await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
            Assert.Equal(0, factory.Users.Calls);
            await AssertAllCapacityAsync(factory);
        }
        finally { factory.Keys.Release(); }
        Assert.Equal(0, factory.Users.Calls);
    }

    [Fact]
    public async Task ParserDisagreementFailsClosedBeforeCanonicalReadsAndUsesGatewayInternalError()
    {
        await using ResponsesAdmissionApiFactory factory = new()
        {
            ProtocolOverride = new ResponsesAdmissionApiFactory.ProbeProtocolAdapter { EffectiveStream = false },
        };
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await SendAsync(client,
            "{\"model\":\"test\",\"input\":\"x\",\"stream\":true}", "application/json", true);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(1, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        await AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task WholePreparationDeadlineIncludesAnUncooperativeStrictParser()
    {
        FakeTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        ResponsesAdmissionApiFactory.ProbeProtocolAdapter parser = new() { Block = true, EffectiveStream = true };
        await using ResponsesAdmissionApiFactory factory = new() { AuthorizationTimeProvider = clock, ProtocolOverride = parser };
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> pending = SendAsync(client,
            "{\"model\":\"test\",\"input\":\"x\",\"stream\":true}", "application/json", true).AsTask();
        await parser.Entered.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await pending.WaitAsync(TestContext.Current.CancellationToken);
            await AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
            await AssertAllCapacityAsync(factory);
        }
        finally { parser.Release(); }
        Assert.Equal(0, factory.Users.Calls);
    }

    [Fact]
    public async Task OversizeStillAuthenticatesBeforePublishingSizeFailure()
    {
        await using ResponsesAdmissionApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        string oversized = new('x', 1_048_577);
        using HttpResponseMessage unauthorized = await SendAsync(client, oversized, "application/json", true);
        await AssertProblemAsync(unauthorized, HttpStatusCode.Unauthorized, "invalid_api_key");
        factory.Keys.Valid = true;
        using HttpResponseMessage rejected = await SendAsync(client, oversized, "application/json", true);
        await AssertProblemAsync(rejected, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Equal(2, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        await AssertAllCapacityAsync(factory);
    }

    private static async ValueTask<HttpResponseMessage> SendAsync(HttpClient client, string body,
        string mediaType, bool authenticated)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/responses");
        if (authenticated) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-key-not-secret"); }
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(json.RootElement.GetProperty("detail").GetString(), json.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(response.Headers.GetValues("X-Request-Id").Single(), json.RootElement.GetProperty("request_id").GetString());
    }

    internal static async ValueTask AssertAllCapacityAsync(ResponsesAdmissionApiFactory factory)
    {
        var discriminator = factory.Services.GetRequiredService<GatewayModelDiscriminator>();
        List<GatewayModelDiscriminatorLease> guards = [];
        try { for (int index = 0; index < 8; index++) { guards.Add(discriminator.TryAcquire(TestContext.Current.CancellationToken).Value); } }
        finally { foreach (var guard in guards) { guard.Dispose(); } }
        var admission = factory.Services.GetRequiredService<GatewayAdmissionController>();
        foreach (GatewayAdmissionKind kind in new[] { GatewayAdmissionKind.NonStream, GatewayAdmissionKind.Sse })
        {
            using GatewayAdmissionLease lease = (await admission.AcquireAsync(kind, TestContext.Current.CancellationToken).ConfigureAwait(false)).Value;
        }
    }
}
