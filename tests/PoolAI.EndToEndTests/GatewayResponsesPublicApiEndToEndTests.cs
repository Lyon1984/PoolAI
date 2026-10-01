using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.EndToEndTests;

[Collection(M2ExitSerialTestGroup.Name)]
public sealed class GatewayResponsesPublicApiEndToEndTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task PublicResponsesUseProductionAuthorizationDispatchSettlementAndLeaseCleanup()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using LoopbackResponsesUpstream upstream = new();
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(cancellationToken, gatewayPeer: true);
        FixtureContext context = await ProvisionAsync(environment, upstream, cancellationToken);
        string text = WireFixture("responses-stream-completed.sse");
        string functions = WireFixture("responses-stream-function-call.sse");
        JsonObject completed = CompletedResponse(text);
        JsonObject noUsage = completed.DeepClone().AsObject();
        noUsage.Remove("usage");
        Case[] cases =
        [
            new(completed.ToJsonString(), false, HttpStatusCode.OK, "upstream", "10", false),
            new(text, true, HttpStatusCode.OK, "upstream", "10", false),
            new(functions, true, HttpStatusCode.OK, "upstream", "58", false),
            new(noUsage.ToJsonString(), false, HttpStatusCode.OK, "conservative_estimate", null, false),
            new(text[..text.LastIndexOf("event: response.completed", StringComparison.Ordinal)],
                true, HttpStatusCode.OK, "conservative_estimate", null, true),
            new(UnsafeUsage(completed), false, HttpStatusCode.BadGateway, "upstream", "9007199254740992", true),
        ];
        foreach (Case testCase in cases)
        {
            upstream.Enqueue(testCase.Body, testCase.Stream);
            using HttpResponseMessage response = await SendAsync(environment.Client, context.ApiKey,
                testCase.Stream, previousResponseId: "resp_0190f901", cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Equal(testCase.Status, response.StatusCode);
            Assert.Equal(testCase.Stream ? "text/event-stream" : "application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("[DONE]", body, StringComparison.Ordinal);
            if (testCase.Stream)
            {
                Assert.Equal(!testCase.Failed, body.Contains("event: response.completed", StringComparison.Ordinal));
                Assert.Equal(testCase.Failed, body.Contains("event: error", StringComparison.Ordinal));
            }
            else if (!testCase.Failed)
            {
                using JsonDocument json = JsonDocument.Parse(body);
                Assert.Equal("gpt-test", json.RootElement.GetProperty("model").GetString());
            }
            await AssertSettlementAsync(environment, context,
                response.Headers.GetValues("X-Request-Id").Single(), testCase, cancellationToken);
            if (!testCase.Failed)
            {
                string id = testCase.Body.Contains("resp_0190f931", StringComparison.Ordinal) ? "resp_0190f931" : "resp_0190f901";
                await AssertAffinityAsync(environment, context, id, cancellationToken);
            }
        }
        Assert.Equal(cases.Length, upstream.Requests.Length);
        Assert.All(upstream.Requests, request => Assert.Equal("resp_0190f901", request.GetProperty("previous_response_id").GetString()));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Redis")]
    public async Task ClientDisconnectDrainsKnownUsageWithoutWritingCompletionOrLeakingLeases()
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = deadline.Token;
        await using LoopbackResponsesUpstream upstream = new();
        await using PasswordResetHttpEndToEndEnvironment environment =
            await PasswordResetHttpEndToEndEnvironment.CreateM2ExitAsync(cancellationToken, gatewayPeer: true);
        FixtureContext context = await ProvisionAsync(environment, upstream, cancellationToken);
        LoopbackResponsesUpstream.ReplyPause pause = upstream.EnqueuePausedBeforeCompletion(WireFixture("responses-stream-completed.sse"));
        using CancellationTokenSource clientAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);
        request.Content = JsonContent.Create(new { model = "gpt-test", input = "text", stream = true, max_output_tokens = 100 });
        using HttpResponseMessage response = await environment.Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, clientAbort.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string requestId = response.Headers.GetValues("X-Request-Id").Single();
        using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] block = new byte[1024];
        int read = await body.ReadAsync(block, cancellationToken);
        Assert.True(read > 0);
        Assert.DoesNotContain("event: response.completed", Encoding.UTF8.GetString(block, 0, read), StringComparison.Ordinal);
        await pause.Reached.Task.WaitAsync(cancellationToken);
        await clientAbort.CancelAsync();
        response.Dispose();
        await environment.GatewayClientDisconnected.WaitAsync(cancellationToken);
        pause.Resume();
        await environment.GatewayRequestCompleted.WaitAsync(cancellationToken);
        await AssertSettlementAsync(environment, context, requestId,
            new Case(string.Empty, true, HttpStatusCode.OK, "upstream", "10", false),
            cancellationToken, expectedOutcome: "cancelled");
        Assert.Single(upstream.Requests);
    }

    internal static async ValueTask<FixtureContext> ProvisionAsync(PasswordResetHttpEndToEndEnvironment environment,
        LoopbackResponsesUpstream upstream, CancellationToken cancellationToken)
    {
        string admin = await M2ExitPublicApiEndToEndTests.LoginAsync(environment, environment.AdminEmail,
            PasswordResetHttpEndToEndEnvironment.OriginalPassword, cancellationToken).ConfigureAwait(false);
        var user = await M2ExitPublicApiEndToEndTests.CreateUserAndLoginAsync(environment, admin, cancellationToken).ConfigureAwait(false);
        var access = await M2ExitPublicApiEndToEndTests.CreateDisabledAccessResourcesAsync(environment, admin, cancellationToken).ConfigureAwait(false);
        var supply = await M2ExitPublicApiEndToEndTests.ProvisionSupplyThroughPublicApiAsync(environment,
            admin, access.GroupId, upstream.BaseAddress, cancellationToken).ConfigureAwait(false);
        using IHost health = environment.CreateSupplyHealthHost();
        await PasswordResetHttpEndToEndEnvironment.RunSupplyHealthRoundAsync(health, cancellationToken).ConfigureAwait(false);
        await M2ExitPublicApiEndToEndTests.WaitForAccountHealthAsync(environment, admin, supply.AccountId, "healthy", cancellationToken).ConfigureAwait(false);
        await M2ExitPublicApiEndToEndTests.ActivateGroupAsync(environment, admin, access.GroupId, cancellationToken).ConfigureAwait(false);
        await M2ExitPublicApiEndToEndTests.AssignSubscriptionAsync(environment, admin, user, access, cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage create = new(HttpMethod.Post, "/api/v1/me/api-keys");
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        create.Headers.Add("Idempotency-Key", "m4-e2-key");
        create.Content = JsonContent.Create(new { name = "M4 E2", group_id = access.GroupId, allowed_cidrs = Array.Empty<string>() });
        using HttpResponseMessage response = await environment.Client.SendAsync(create, cancellationToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument key = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return new FixtureContext(access.GroupId, supply.AccountId,
            key.RootElement.GetProperty("api_key").GetProperty("id").GetGuid(), admin,
            key.RootElement.GetProperty("secret").GetString()!);
    }

    private static async ValueTask<HttpResponseMessage> SendAsync(HttpClient client, string apiKey,
        bool stream, string? previousResponseId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new { model = "gpt-test", input = "text", stream,
            previous_response_id = previousResponseId, max_output_tokens = 100 });
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask AssertSettlementAsync(PasswordResetHttpEndToEndEnvironment environment,
        FixtureContext context, string requestId, Case testCase, CancellationToken cancellationToken,
        string? expectedOutcome = null)
    {
        using NpgsqlCommand command = environment.AdministratorDataSource.CreateCommand("""
            SELECT reservation.status, reservation.actual_tokens::text, reservation.usage_source,
                   attempt.status, request.status, request.attempt_count, attempt.attempt_index,
                   attempt.account_id, period.reserved_tokens::text,
                   (SELECT count(*) FROM public.usage_attempts a WHERE a.request_id = request.request_id),
                   (SELECT count(*) FROM public.group_token_reservations r WHERE r.request_id = request.request_id)
            FROM public.usage_requests request
            JOIN public.usage_attempts attempt ON attempt.request_id = request.request_id
            JOIN public.group_token_reservations reservation ON reservation.attempt_id = attempt.attempt_id
            JOIN public.group_quota_periods period ON period.id = reservation.period_id
            WHERE request.request_id = $1 AND attempt.account_id = $2 AND reservation.group_id = $3;
            """);
        command.Parameters.AddWithValue(Guid.Parse(requestId));
        command.Parameters.AddWithValue(context.AccountId);
        command.Parameters.AddWithValue(context.GroupId);
        using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal("settled", reader.GetString(0));
        if (testCase.Tokens is not null) { Assert.Equal(testCase.Tokens, reader.GetString(1)); }
        else { Assert.True(System.Numerics.BigInteger.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture) > 0); }
        Assert.Equal(testCase.Source, reader.GetString(2));
        Assert.Equal(expectedOutcome ?? (testCase.Failed ? "failed" : "succeeded"), reader.GetString(3));
        Assert.Equal(expectedOutcome ?? (testCase.Failed ? "failed" : "succeeded"), reader.GetString(4));
        Assert.Equal(1, reader.GetInt32(5));
        Assert.Equal(0, reader.GetInt32(6));
        Assert.Equal(context.AccountId, reader.GetGuid(7));
        Assert.Equal("0", reader.GetString(8));
        Assert.Equal(1, reader.GetInt64(9));
        Assert.Equal(1, reader.GetInt64(10));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        Assert.Equal(0, await M2ExitPublicApiEndToEndTests.ReadAccountActiveLeasesAsync(environment,
            context.AdminToken, context.AccountId, cancellationToken).ConfigureAwait(false));
    }

    internal static string WireFixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PoolAI.sln"))) { directory = directory.Parent; }
        return File.ReadAllText(Path.Combine(directory!.FullName, "docs", "contracts", "fixtures", name)).TrimEnd('\n') + "\n\n";
    }

    private static JsonObject CompletedResponse(string fixture) => JsonNode.Parse(fixture.Split('\n')
        .Last(static line => line.StartsWith("data: ", StringComparison.Ordinal))[6..])!["response"]!.AsObject();

    private static string UnsafeUsage(JsonObject completed)
    {
        JsonObject result = completed.DeepClone().AsObject();
        result["usage"] = JsonNode.Parse("{\"input_tokens\":9007199254740992,\"output_tokens\":0,\"total_tokens\":9007199254740992}");
        return result.ToJsonString();
    }

    private static async ValueTask AssertAffinityAsync(PasswordResetHttpEndToEndEnvironment environment,
        FixtureContext context, string responseId, CancellationToken cancellationToken)
    {
        IConfiguration configuration = environment.Services.GetRequiredService<IConfiguration>();
        byte[] pepper = Convert.FromBase64String(configuration["Idempotency:RequestHashPepper"]!);
        byte[] input = Encoding.UTF8.GetBytes($"PoolAI:ResponsesAffinity:v1\0{context.GroupId:D}\0{context.ApiKeyId:D}\0{responseId}");
        string hash;
        try { hash = Convert.ToHexStringLower(HMACSHA256.HashData(pepper, input).AsSpan(0, 16)); }
        finally { CryptographicOperations.ZeroMemory(pepper); CryptographicOperations.ZeroMemory(input); }
        var store = environment.Services.GetRequiredService<IRouteAffinityStore>();
        RouteAffinity? affinity = await store.GetAsync(new EntityId(context.GroupId), hash, cancellationToken).ConfigureAwait(false);
        Assert.NotNull(affinity);
        Assert.Equal(new EntityId(context.AccountId), affinity.AccountId);
        Assert.Equal(2, affinity.GroupPolicyVersion);
        Assert.True(affinity.SupplyConfigurationVersion > 1);
    }

    internal sealed class FixtureContext(Guid groupId, Guid accountId, Guid apiKeyId, string adminToken, string apiKey)
    {
        internal Guid GroupId { get; } = groupId;
        internal Guid AccountId { get; } = accountId;
        internal Guid ApiKeyId { get; } = apiKeyId;
        internal string AdminToken { get; } = adminToken;
        internal string ApiKey { get; } = apiKey;
    }
    internal sealed record Case(string Body, bool Stream, HttpStatusCode Status, string Source, string? Tokens, bool Failed);
}
