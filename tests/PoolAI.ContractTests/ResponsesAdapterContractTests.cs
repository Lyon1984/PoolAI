using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Adapters.OpenAI;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

// Governing contracts: M4-E2, AC-028/045, canonical Responses fixtures and error catalog.
public sealed class ResponsesAdapterContractTests
{
    [Theory]
    [InlineData("responses-stream-completed.sse", 1)]
    [InlineData("responses-stream-completed.sse", 7)]
    [InlineData("responses-stream-completed.sse", 4096)]
    [InlineData("responses-stream-function-call.sse", 1)]
    [InlineData("responses-stream-function-call.sse", 13)]
    public async Task CanonicalTypedStreamsSurviveHalfPacketsAndHoldTheTerminalEvent(string fixture, int chunkSize)
    {
        ResponsesTestAssets.RecordingOutput output = new();
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(true, output);
        using var content = new ResponsesTestAssets.ChunkedStream(ResponsesTestAssets.WireFixture(fixture), chunkSize);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.ErrorCode);
        Assert.NotNull(result.Value.Usage);
        Assert.Equal("client-alias", result.Value.Payload.GetProperty("model").GetString());
        Assert.Equal(output.Events.Count, result.Value.TerminalEvent!.Value.GetProperty("sequence_number").GetInt32());
        Assert.DoesNotContain(output.Events, e => e.GetProperty("type").GetString() is "response.completed" or "error");
        Assert.Equal("response.completed", result.Value.TerminalEvent.Value.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false, UpstreamType.OpenAi)]
    [InlineData(true, UpstreamType.OpenAi)]
    [InlineData(false, UpstreamType.OpenAiCompatible)]
    [InlineData(true, UpstreamType.OpenAiCompatible)]
    public async Task PreparedRequestIsMappedBoundAndSingleUse(bool stream, UpstreamType upstream)
    {
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(stream, upstream: upstream);
        var created = await prepared.CreateRequestAsync(TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);
        using PreparedUpstreamRequest request = created.Value;
        Assert.Equal("https://upstream.example/v1/responses", request.RequestUri.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, request.Method);
        using JsonDocument document = JsonDocument.Parse(request.Body);
        Assert.Equal("upstream-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(stream, document.RootElement.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain(request.Headers, h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.True((await prepared.CreateRequestAsync(TestContext.Current.CancellationToken)).IsFailure);
    }

    [Theory]
    [InlineData(401, "upstream_auth_failed")]
    [InlineData(403, "upstream_auth_failed")]
    [InlineData(429, "upstream_rejected")]
    [InlineData(400, "upstream_rejected")]
    [InlineData(408, "upstream_dispatch_ambiguous")]
    [InlineData(500, "upstream_dispatch_ambiguous")]
    public async Task UpstreamErrorsNeverExposeTheProviderBody(int status, string code)
    {
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(false);
        using MemoryStream content = new("provider-sensitive-detail"u8.ToArray());
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false, status), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(code, result.Value.ErrorCode);
        Assert.Null(result.Value.Usage);
        Assert.Equal(JsonValueKind.Undefined, result.Value.Payload.ValueKind);
        Assert.Equal("req_responses_test", result.Value.UpstreamRequestId);
    }

    [Theory]
    [InlineData("responses-stream-error.sse")]
    [InlineData("responses-stream-first-byte-timeout.sse")]
    [InlineData("responses-stream-usage-out-of-range.sse")]
    public async Task ProviderErrorFramesAreSanitizedAndNeverFinishSuccessfully(string fixture)
    {
        ResponsesTestAssets.RecordingOutput output = new();
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(true, output);
        using var content = new ResponsesTestAssets.ChunkedStream(ResponsesTestAssets.WireFixture(fixture), 3);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_stream_error", result.Value.ErrorCode);
        Assert.Null(result.Value.TerminalEvent);
        Assert.DoesNotContain(output.Events, e => e.GetProperty("type").GetString() is "error" or "response.completed");
    }

    [Fact]
    public async Task NonStreamNormalizesCacheDetailsAndMissingUsageConservatively()
    {
        foreach (bool missing in new[] { false, true })
        {
            JsonObject response = JsonNode.Parse(ResponsesTestAssets.CompletedResponse().GetRawText())!.AsObject();
            if (missing) { response.Remove("usage"); }
            else { response["usage"]!["input_tokens_details"]!.AsObject().Remove("cache_write_tokens"); }
            await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(false);
            using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 2);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Null(result.Value.ErrorCode);
            if (missing) { Assert.Null(result.Value.Usage); Assert.Equal(JsonValueKind.Null, result.Value.Payload.GetProperty("usage").ValueKind); }
            else { Assert.Equal(BigInteger.Zero, result.Value.Usage!.CacheCreationTokens); }
            Assert.True((await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken)).IsFailure);
        }
    }

    [Theory]
    [InlineData("9007199254740992", "0")]
    [InlineData("999999999999999999999999999999999999999999999999999999999999999999999999999999999", "0")]
    public async Task UnsafeUsageRemainsLosslessAndIsNotAProtocolFailure(string input, string output)
    {
        string body = ResponsesTestAssets.CompletedResponse().GetRawText();
        JsonObject response = JsonNode.Parse(body)!.AsObject();
        response["usage"] = JsonNode.Parse($"{{\"input_tokens\":{input},\"output_tokens\":{output},\"total_tokens\":{input}}}");
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 1);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_usage_out_of_range", result.Value.ErrorCode);
        Assert.Equal(BigInteger.Parse(input, System.Globalization.CultureInfo.InvariantCulture), result.Value.Usage!.InputTokens);
        Assert.False(result.Value.Usage.IsOpenAiSafeIntegerShape);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("1e1")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public async Task UsageCountersRejectNonCanonicalIntegerLexemes(string counter)
    {
        JsonObject response = JsonNode.Parse(ResponsesTestAssets.CompletedResponse().GetRawText())!.AsObject();
        response["usage"] = JsonNode.Parse($"{{\"input_tokens\":{counter},\"output_tokens\":0,\"total_tokens\":1}}");
        await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 7);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Fact]
    public async Task StreamEofAndIncompleteFramesCannotCreateSuccessfulCompletion()
    {
        string stream = ResponsesTestAssets.WireFixture("responses-stream-completed.sse");
        foreach (string broken in new[] { stream[..stream.LastIndexOf("event: response.completed", StringComparison.Ordinal)], stream[..^10] })
        {
            await using IPreparedUpstreamAttempt prepared = await ResponsesTestAssets.PrepareAsync(true);
            using var content = new ResponsesTestAssets.ChunkedStream(broken, 5);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure || result.Value.ErrorCode is not null);
            if (result.IsSuccess) { Assert.Null(result.Value.TerminalEvent); }
        }
    }
}
