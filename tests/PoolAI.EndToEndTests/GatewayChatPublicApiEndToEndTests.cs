using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Npgsql;
using Assets = PoolAI.EndToEndTests.GatewayResponsesPublicApiEndToEndTests;

namespace PoolAI.EndToEndTests;

[Collection(M2ExitSerialTestGroup.Name)]
public sealed class GatewayChatPublicApiEndToEndTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ChatVerticalSliceClosesQuotaUsageAndAudit()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using LoopbackResponsesUpstream upstream = new("/chat/completions");
        await using var environment = await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(cancellationToken, gatewayPeer: true);
        Assets.FixtureContext context = await Assets.ProvisionAsync(environment, upstream, cancellationToken);
        string text = Assets.WireFixture("chat-completions-text.sse");
        string functions = Assets.WireFixture("chat-completions-function-call.sse");
        JsonObject completed = Completion();
        JsonObject missing = completed.DeepClone().AsObject();
        missing.Remove("usage");
        JsonObject unsafeUsage = completed.DeepClone().AsObject();
        unsafeUsage["usage"] = JsonNode.Parse("{\"prompt_tokens\":9007199254740992,\"completion_tokens\":0,\"total_tokens\":9007199254740992}");
        Assets.Case[] cases =
        [
            new(completed.ToJsonString(), false, HttpStatusCode.OK, "upstream", "10", false),
            new(text, true, HttpStatusCode.OK, "upstream", "10", false),
            new(functions, true, HttpStatusCode.OK, "upstream", "58", false),
            new(missing.ToJsonString(), false, HttpStatusCode.OK, "conservative_estimate", null, false),
            new(text[..text.LastIndexOf("data: [DONE]", StringComparison.Ordinal)], true,
                HttpStatusCode.OK, "upstream", "10", true),
            new(unsafeUsage.ToJsonString(), false, HttpStatusCode.BadGateway, "upstream", "9007199254740992", true),
        ];
        foreach (Assets.Case item in cases)
        {
            upstream.Enqueue(item.Body, item.Stream);
            using HttpResponseMessage response = await SendAsync(environment.Client, context.ApiKey, item.Stream, true, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(item.Status, response.StatusCode);
            Assert.Equal(item.Stream ? "text/event-stream" : "application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("event:", body, StringComparison.Ordinal);
            if (item.Stream)
            {
                Assert.Equal(!item.Failed, body.Contains("data: [DONE]", StringComparison.Ordinal));
                Assert.Equal(item.Failed, body.Contains("\"error\":", StringComparison.Ordinal));
                if (!item.Failed) { Assert.Contains("\"choices\":[],\"model\":\"gpt-test\",\"usage\":", body, StringComparison.Ordinal); }
            }
            else if (!item.Failed)
            {
                using JsonDocument json = JsonDocument.Parse(body);
                Assert.Equal("gpt-test", json.RootElement.GetProperty("model").GetString());
            }
            string requestId = response.Headers.GetValues("X-Request-Id").Single();
            await Assets.AssertSettlementAsync(environment, context, requestId, item, cancellationToken);
            await AssertPublicationFactsAsync(environment, requestId, cancellationToken);
        }
        Assert.Equal(cases.Length, upstream.Requests.Length);
        Assert.All(upstream.Requests, request => Assert.True(request.TryGetProperty("messages", out _)));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PublicIncludeUsageFalseHidesAccountingChunkAndMissingUsageSettlesConservatively()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using LoopbackResponsesUpstream upstream = new("/chat/completions");
        await using var environment = await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(cancellationToken, gatewayPeer: true);
        Assets.FixtureContext context = await Assets.ProvisionAsync(environment, upstream, cancellationToken);
        foreach (bool known in new[] { true, false })
        {
            upstream.Enqueue(Assets.WireFixture(known ? "chat-completions-text.sse" : "chat-completions-text-no-usage.sse"), true);
            using HttpResponseMessage response = await SendAsync(environment.Client, context.ApiKey, true, false, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"choices\":[]", body, StringComparison.Ordinal);
            await Assets.AssertSettlementAsync(environment, context, response.Headers.GetValues("X-Request-Id").Single(),
                new Assets.Case(string.Empty, true, HttpStatusCode.OK, known ? "upstream" : "conservative_estimate", known ? "10" : null, false), cancellationToken);
        }
        Assert.All(upstream.Requests, request => Assert.True(request.GetProperty("stream_options").GetProperty("include_usage").GetBoolean()));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ChatClientDisconnectDrainsUsageAndNeverLeaksALease()
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = deadline.Token;
        await using LoopbackResponsesUpstream upstream = new("/chat/completions");
        await using var environment = await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(cancellationToken, gatewayPeer: true);
        Assets.FixtureContext context = await Assets.ProvisionAsync(environment, upstream, cancellationToken);
        LoopbackResponsesUpstream.ReplyPause pause = upstream.EnqueuePausedBeforeCompletion(
            Assets.WireFixture("chat-completions-text.sse"), "data: {\"id\":\"chatcmpl_0190f921\",\"object\":\"chat.completion.chunk\",\"created\":1783987200,\"model\":\"gpt-5\",\"choices\":[]");
        using CancellationTokenSource clientAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using HttpRequestMessage request = Request(context.ApiKey, true, true);
        using HttpResponseMessage response = await environment.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, clientAbort.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string requestId = response.Headers.GetValues("X-Request-Id").Single();
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] block = new byte[1024];
        int read = await body.ReadAsync(block, cancellationToken);
        Assert.True(read > 0);
        Assert.DoesNotContain("[DONE]", Encoding.UTF8.GetString(block, 0, read), StringComparison.Ordinal);
        await pause.Reached.Task.WaitAsync(cancellationToken);
        await clientAbort.CancelAsync();
        response.Dispose();
        await environment.GatewayClientDisconnected.WaitAsync(cancellationToken);
        pause.Resume();
        await environment.GatewayRequestCompleted.WaitAsync(cancellationToken);
        await Assets.AssertSettlementAsync(environment, context, requestId,
            new Assets.Case(string.Empty, true, HttpStatusCode.OK, "upstream", "10", false), cancellationToken, "cancelled");
    }

    private static HttpRequestMessage Request(string key, bool stream, bool includeUsage)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { model = "gpt-test", messages = new[] { new { role = "user", content = "text" } },
            stream, max_completion_tokens = 100, stream_options = new { include_usage = includeUsage } });
        return request;
    }
    private static async ValueTask<HttpResponseMessage> SendAsync(HttpClient client, string key,
        bool stream, bool includeUsage, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(key, stream, includeUsage);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
    private static JsonObject Completion() => JsonSerializer.SerializeToNode(new
    {
        id = "chat_fixture", @object = "chat.completion", created = 1783987200, model = "provider-model",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "text" }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 8, completion_tokens = 2, total_tokens = 10 },
    })!.AsObject();

    private static async ValueTask AssertPublicationFactsAsync(PasswordResetHttpEndToEndEnvironment environment,
        string requestId, CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM public.group_quota_events e
                    JOIN public.group_token_reservations r ON r.attempt_id = e.attempt_id
                    WHERE r.request_id = $1 AND e.event_type = 'settled'),
                   (SELECT count(*) FROM public.outbox_messages WHERE correlation_id = $1 AND topic = 'poolai.quota.v1'),
                   (SELECT count(*) FROM public.audit_logs WHERE request_id = $1);
            """);
        command.Parameters.AddWithValue(Guid.Parse(requestId));
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(1, reader.GetInt64(0));
        Assert.True(reader.GetInt64(1) >= 3);
        Assert.True(reader.GetInt64(2) >= 1);
    }
}
