using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using AdmissionAssertions = PoolAI.EndToEndTests.ResponsesAdmissionHttpTests;

namespace PoolAI.EndToEndTests;

public sealed class ChatAdmissionHttpTests
{
    [Theory]
    [InlineData("{\"stream\":true}", GatewayAdmissionKind.Sse)]
    [InlineData("{\"stream\":false}", GatewayAdmissionKind.NonStream)]
    [InlineData("{}", GatewayAdmissionKind.NonStream)]
    [InlineData("{\"stream\":true,\"stream\":false}", GatewayAdmissionKind.Sse)]
    [InlineData("{\"stream\":0}", GatewayAdmissionKind.NonStream)]
    [InlineData("{", GatewayAdmissionKind.NonStream)]
    public async Task ChatSelectedPartitionRejectsBeforeAuthAndBodyErrors(string body, GatewayAdmissionKind kind)
    {
        await using ResponsesAdmissionApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var admission = factory.Services.GetRequiredService<GatewayAdmissionController>();
        using GatewayAdmissionLease held = (await admission.AcquireAsync(kind, TestContext.Current.CancellationToken)).Value;
        using HttpResponseMessage response = await SendAsync(client, body, false);
        await AdmissionAssertions.AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal(0, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        GatewayAdmissionKind other = kind == GatewayAdmissionKind.Sse ? GatewayAdmissionKind.NonStream : GatewayAdmissionKind.Sse;
        using GatewayAdmissionLease independent = (await admission.AcquireAsync(other, TestContext.Current.CancellationToken)).Value;
    }

    [Theory]
    [InlineData("{", false, 401, "invalid_api_key")]
    [InlineData("{", true, 400, "invalid_request")]
    [InlineData("{\"model\":\"test\",\"messages\":[]}", true, 422, "validation_failed")]
    [InlineData("{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"stream\":null}", true, 422, "validation_failed")]
    [InlineData("{\"stream\":true,\"stream\":false}", true, 400, "invalid_request")]
    [InlineData("{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"functions\":[]}", true, 400, "unsupported_feature")]
    public async Task ChatAuthenticationPrecedesStrictSyntaxAndSchema(string body, bool validKey, int status, string code)
    {
        await using ResponsesAdmissionApiFactory factory = new();
        factory.Keys.Valid = validKey;
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await SendAsync(client, body, true);
        await AdmissionAssertions.AssertProblemAsync(response, (HttpStatusCode)status, code);
        Assert.Equal(1, factory.Keys.Calls);
        Assert.Equal(0, factory.Users.Calls);
        await AdmissionAssertions.AssertAllCapacityAsync(factory);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"stream\":false")]
    [InlineData(",\"stream\":true")]
    public async Task ChatAuthoritativeParserEntersCanonicalAuthorizationOnce(string mode)
    {
        await using ResponsesAdmissionApiFactory factory = new();
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await SendAsync(client,
            "{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]" + mode + "}", true);
        await AdmissionAssertions.AssertProblemAsync(response, HttpStatusCode.Forbidden, "user_disabled");
        Assert.Equal(1, factory.Users.Calls);
        await AdmissionAssertions.AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task ChatGuardSaturationNeverEntersAuthOrConsumesDataCapacity()
    {
        await using ResponsesAdmissionApiFactory factory = new();
        using HttpClient client = factory.CreateClient();
        var discriminator = factory.Services.GetRequiredService<GatewayModelDiscriminator>();
        List<GatewayModelDiscriminatorLease> guards = [];
        try
        {
            for (int index = 0; index < 8; index++) { guards.Add(discriminator.TryAcquire(TestContext.Current.CancellationToken).Value); }
            using HttpResponseMessage response = await SendAsync(client, "{", false);
            await AdmissionAssertions.AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
            Assert.Equal(0, factory.Keys.Calls);
            Assert.Equal(0, factory.Users.Calls);
        }
        finally { foreach (var guard in guards) { guard.Dispose(); } }
        await AdmissionAssertions.AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task ChatDiscriminatorAndStrictParserDisagreementFailsClosed()
    {
        await using ResponsesAdmissionApiFactory factory = new()
        {
            ProtocolOverride = new ResponsesAdmissionApiFactory.ProbeProtocolAdapter
            { Protocol = InboundProtocol.ChatCompletions, EffectiveStream = false },
        };
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await SendAsync(client, "{\"stream\":true}", true);
        await AdmissionAssertions.AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(0, factory.Users.Calls);
        await AdmissionAssertions.AssertAllCapacityAsync(factory);
    }

    [Fact]
    public async Task ChatPreparationDeadlineFencesAnUncooperativeParser()
    {
        FakeTimeProvider clock = new(TimeProvider.System.GetUtcNow());
        ResponsesAdmissionApiFactory.ProbeProtocolAdapter parser = new()
        { Protocol = InboundProtocol.ChatCompletions, Block = true, EffectiveStream = true };
        await using ResponsesAdmissionApiFactory factory = new() { AuthorizationTimeProvider = clock, ProtocolOverride = parser };
        factory.Keys.Valid = true;
        using HttpClient client = factory.CreateClient();
        Task<HttpResponseMessage> pending = SendAsync(client, "{\"stream\":true}", true).AsTask();
        await parser.Entered.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            using HttpResponseMessage response = await pending.WaitAsync(TestContext.Current.CancellationToken);
            await AdmissionAssertions.AssertProblemAsync(response, HttpStatusCode.TooManyRequests, "gateway_overloaded");
            await AdmissionAssertions.AssertAllCapacityAsync(factory);
        }
        finally { parser.Release(); }
        Assert.Equal(0, factory.Users.Calls);
    }

    private static async ValueTask<HttpResponseMessage> SendAsync(HttpClient client, string body, bool authenticated)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions");
        if (authenticated) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-key-not-secret"); }
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
