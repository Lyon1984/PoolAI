using System.Numerics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

public sealed class ChatAdapterContractTests
{
    [Theory]
    [InlineData("chat-completions-text.sse", 1, true, 10)]
    [InlineData("chat-completions-text.sse", 7, true, 10)]
    [InlineData("chat-completions-text.sse", 4096, true, 10)]
    [InlineData("chat-completions-function-call.sse", 1, true, 58)]
    [InlineData("chat-completions-function-call.sse", 13, true, 58)]
    [InlineData("chat-completions-text-no-usage.sse", 1, false, 0)]
    [InlineData("chat-completions-text.sse", 3, false, 10)]
    public async Task CanonicalStreamsSurviveHalfPacketsAndHoldUsageAndDoneUntilSettlement(
        string fixture, int chunkSize, bool includeUsage, int tokens)
    {
        ChatTestAssets.RecordingOutput output = new();
        await using var prepared = await ChatTestAssets.PrepareAsync(true, includeUsage, output);
        using var content = new ResponsesTestAssets.ChunkedStream(ResponsesTestAssets.WireFixture(fixture), chunkSize);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ErrorCode);
        Assert.True(result.Value.StreamCompleted);
        Assert.Equal(includeUsage, result.Value.TerminalEvent is not null);
        Assert.NotEmpty(output.Events);
        Assert.All(output.Events, frame =>
        {
            Assert.Equal("client-alias", frame.GetProperty("model").GetString());
            Assert.NotEmpty(frame.GetProperty("choices").EnumerateArray());
            if (includeUsage) { Assert.Equal(JsonValueKind.Null, frame.GetProperty("usage").ValueKind); }
        });
        if (tokens == 0) { Assert.Null(result.Value.Usage); }
        else { Assert.Equal(tokens, result.Value.Usage!.TotalTokens); }
        if (includeUsage) { Assert.Empty(result.Value.TerminalEvent!.Value.GetProperty("choices").EnumerateArray()); }
    }

    [Theory]
    [InlineData(false, UpstreamType.OpenAi)]
    [InlineData(true, UpstreamType.OpenAi)]
    [InlineData(false, UpstreamType.OpenAiCompatible)]
    [InlineData(true, UpstreamType.OpenAiCompatible)]
    public async Task PreparedRequestBindsModelPathAndAccountingAndIsSingleUse(bool stream, UpstreamType provider)
    {
        await using var prepared = await ChatTestAssets.PrepareAsync(stream, upstream: provider);
        var result = await prepared.CreateRequestAsync(TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        using PreparedUpstreamRequest request = result.Value;
        Assert.Equal("https://upstream.example/v1/chat/completions", request.RequestUri.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal("upstream-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(stream, body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(stream, body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.DoesNotContain(request.Headers, h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.True((await prepared.CreateRequestAsync(TestContext.Current.CancellationToken)).IsFailure);
    }

    [Theory]
    [InlineData(401, "upstream_auth_failed")]
    [InlineData(403, "upstream_auth_failed")]
    [InlineData(400, "upstream_rejected")]
    [InlineData(429, "upstream_rejected")]
    [InlineData(408, "upstream_dispatch_ambiguous")]
    [InlineData(500, "upstream_dispatch_ambiguous")]
    public async Task ChatErrorsUseOnlyTheGatewaySchema(int status, string code)
    {
        await using var prepared = await ChatTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream("provider-sensitive-detail", 1);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false, status), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(code, result.Value.ErrorCode);
        Assert.Null(result.Value.Usage);
        Assert.Equal(JsonValueKind.Undefined, result.Value.Payload.ValueKind);
    }

    [Fact]
    public async Task NonStreamTextFunctionAndMissingUsageRemainCanonical()
    {
        foreach (string mode in new[] { "text", "tools", "no_usage" })
        {
            JsonObject completion = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
            if (mode is "tools")
            {
                JsonObject followup = JsonNode.Parse(ResponsesTestAssets.Fixture("chat-completions-tool-followup.json"))!.AsObject();
                completion["choices"]![0]!["message"] = followup["messages"]!.AsArray().Single(node =>
                    node!["role"]!.GetValue<string>() is "assistant")!.DeepClone();
                completion["choices"]![0]!["finish_reason"] = "tool_calls";
            }
            if (mode is "no_usage") { completion.Remove("usage"); }
            else { completion["usage"]!["prompt_tokens_details"] = JsonNode.Parse("{\"cached_tokens\":2}"); }
            await using var prepared = await ChatTestAssets.PrepareAsync(false);
            using var content = new ResponsesTestAssets.ChunkedStream(completion.ToJsonString(), 2);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Null(result.Value.ErrorCode);
            Assert.Equal("client-alias", result.Value.Payload.GetProperty("model").GetString());
            if (mode is "no_usage") { Assert.Null(result.Value.Usage); Assert.False(result.Value.Payload.TryGetProperty("usage", out _)); }
            else
            {
                Assert.Equal(2, result.Value.Usage!.CacheReadTokens);
                Assert.False(result.Value.Payload.GetProperty("usage").GetProperty("prompt_tokens_details").TryGetProperty("cache_write_tokens", out _));
            }
            Assert.True((await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken)).IsFailure);
        }
    }

    [Theory]
    [InlineData("9007199254740992")]
    [InlineData("999999999999999999999999999999999999999999999999999999999999999999999999999999999")]
    public async Task UnsafeUsageIsLosslessInternallyAndNeverCompletesPublicly(string number)
    {
        foreach (bool stream in new[] { false, true })
        {
            string usage = "{\"prompt_tokens\":" + number + ",\"completion_tokens\":0,\"total_tokens\":" + number + "}";
            string body;
            if (stream)
            {
                body = MutateFixture("chat-completions-text.sse", 3, frame => frame["usage"] = JsonNode.Parse(usage));
            }
            else
            {
                JsonObject completed = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
                completed["usage"] = JsonNode.Parse(usage);
                body = completed.ToJsonString();
            }
            await using var prepared = await ChatTestAssets.PrepareAsync(stream, includeUsage: true);
            using var content = new ResponsesTestAssets.ChunkedStream(body, 1);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, stream), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Equal("upstream_usage_out_of_range", result.Value.ErrorCode);
            Assert.Equal(BigInteger.Parse(number, CultureInfo.InvariantCulture), result.Value.Usage!.InputTokens);
            Assert.False(result.Value.StreamCompleted);
            Assert.Null(result.Value.TerminalEvent);
        }
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("1e1")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public async Task UsageCountersRejectNonCanonicalIntegerLexemes(string counter)
    {
        JsonObject body = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
        body["usage"]!["prompt_tokens"] = JsonNode.Parse(counter);
        await using var prepared = await ChatTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(body.ToJsonString(), 3);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Theory]
    [InlineData("{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":11}")]
    [InlineData("{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":10,\"prompt_tokens_details\":null}")]
    [InlineData("{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":9}}")]
    [InlineData("{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":5,\"cache_write_tokens\":4}}")]
    [InlineData("{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":10,\"completion_tokens_details\":{\"reasoning_tokens\":3}}")]
    public async Task UsageRelationsCannotBecomeReliableAccountingEvidence(string usage)
    {
        JsonObject body = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
        body["usage"] = JsonNode.Parse(usage);
        await using var prepared = await ChatTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(body.ToJsonString(), 2);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    internal static string MutateFixture(string name, int index, Action<JsonObject> change)
    {
        string[] frames = ResponsesTestAssets.WireFixture(name).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        JsonObject frame = JsonNode.Parse(frames[index][6..])!.AsObject();
        change(frame);
        frames[index] = "data: " + frame.ToJsonString();
        return string.Join("\n\n", frames) + "\n\n";
    }
}
