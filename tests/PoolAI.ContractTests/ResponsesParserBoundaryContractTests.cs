using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

public sealed class ResponsesParserBoundaryContractTests
{
    [Theory]
    [InlineData("object", "\"invalid\"")]
    [InlineData("created_at", "-1")]
    [InlineData("status", "\"in_progress\"")]
    [InlineData("error", "{\"message\":\"private\"}")]
    [InlineData("incomplete_details", "true")]
    [InlineData("instructions", "1")]
    [InlineData("metadata", "{\"tag\":1}")]
    [InlineData("output", "null")]
    [InlineData("parallel_tool_calls", "0")]
    [InlineData("temperature", "true")]
    [InlineData("temperature", "2.1")]
    [InlineData("top_p", "-1")]
    [InlineData("tool_choice", "\"invalid\"")]
    [InlineData("tools", "null")]
    [InlineData("tools", "[{\"type\":\"web_search\"}]")]
    public async Task NonStreamResponseSchemaDoesNotForwardMalformedSuccess(string field, string value)
    {
        JsonObject response = JsonNode.Parse(ResponsesTestAssets.CompletedResponse().GetRawText())!.AsObject();
        response[field] = JsonNode.Parse(value);
        await using var prepared = await ResponsesTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 3);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task FrozenTerminalStatusesPreserveUsageAndDoNotExposeProviderErrors(string status)
    {
        JsonObject response = JsonNode.Parse(ResponsesTestAssets.CompletedResponse().GetRawText())!.AsObject();
        response["status"] = status;
        response["temperature"] = null;
        response["top_p"] = null;
        response["metadata"] = null;
        bool rejected = status is "failed" or "cancelled";
        if (rejected) { response["error"] = JsonNode.Parse("{\"message\":\"provider-private-error\"}"); }
        await using var prepared = await ResponsesTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 1);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value.Usage!.TotalTokens);
        Assert.Equal(rejected ? "upstream_rejected" : null, result.Value.ErrorCode);
        if (rejected) { Assert.Equal(JsonValueKind.Undefined, result.Value.Payload.ValueKind); }
    }

    [Theory]
    [InlineData(0, "sequence_number", "1")]
    [InlineData(0, "type", "\"response.in_progress\"")]
    [InlineData(2, "output_index", "-1")]
    [InlineData(3, "content_index", "-1")]
    [InlineData(4, "delta", "null")]
    [InlineData(4, "logprobs", "null")]
    [InlineData(4, "logprobs", "[1]")]
    [InlineData(4, "extra", "\"private\"")]
    [InlineData(5, "text", "false")]
    public async Task TypedSseUnionAndSequenceRejectMalformedFrames(int eventIndex, string field, string value)
    {
        string[] frames = ResponsesTestAssets.WireFixture("responses-stream-completed.sse").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        string[] lines = frames[eventIndex].Split('\n');
        JsonObject data = JsonNode.Parse(lines[1][6..])!.AsObject();
        data[field] = JsonNode.Parse(value);
        frames[eventIndex] = lines[0] + "\ndata: " + data.ToJsonString();
        await using var prepared = await ResponsesTestAssets.PrepareAsync(true);
        using var content = new ResponsesTestAssets.ChunkedStream(string.Join("\n\n", frames) + "\n\n", 2);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Theory]
    [InlineData("annotations")]
    [InlineData("logprobs")]
    public async Task OutputPartCollectionsContainObjectsRatherThanArbitraryJson(string field)
    {
        JsonObject response = JsonNode.Parse(ResponsesTestAssets.CompletedResponse().GetRawText())!.AsObject();
        response["output"]![0]!["content"]![0]![field] = new JsonArray(1);
        await using var prepared = await ResponsesTestAssets.PrepareAsync(false);
        using var content = new ResponsesTestAssets.ChunkedStream(response.ToJsonString(), 3);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, false), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Theory]
    [InlineData("event: response.created\nevent: response.created\ndata: {}\n\n")]
    [InlineData("id: 1\nevent: response.created\ndata: {}\n\n")]
    [InlineData("data: {}\n\n")]
    [InlineData("event: response.created\ndata: {}\n")]
    [InlineData("event: response.unknown\ndata: {\"type\":\"response.unknown\",\"sequence_number\":0}\n\n")]
    public async Task MalformedWireFramesAreNotPartiallyAccepted(string wire)
    {
        await using var prepared = await ResponsesTestAssets.PrepareAsync(true);
        using var content = new ResponsesTestAssets.ChunkedStream(wire, 1);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task ParserBoundsAndTransportReadFailuresAreSafeAndCancellationIsNotSwallowed()
    {
        foreach ((bool stream, string body) in new[] { (false, new string(' ', 16 * 1024 * 1024 + 1)),
            (true, "data: " + new string('x', 1024 * 1024 + 1)) })
        {
            await using var prepared = await ResponsesTestAssets.PrepareAsync(stream);
            using var content = new ResponsesTestAssets.ChunkedStream(body, 4096);
            Assert.True((await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, stream), TestContext.Current.CancellationToken)).IsFailure);
        }
        foreach (string code in new[] { "upstream_first_byte_timeout", "upstream_stream_idle_timeout", "io" })
        {
            await using var prepared = await ResponsesTestAssets.PrepareAsync(true);
            using FailureStream content = new(code is "io" ? new IOException() : new UpstreamReadTimeoutException(code));
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Equal(code is "io" ? "upstream_stream_error" : code, result.Value.ErrorCode);
        }
        await using var cancelled = await ResponsesTestAssets.PrepareAsync(false);
        using MemoryStream empty = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.ParseResponseAsync(ResponsesTestAssets.Response(empty, false), cancellation.Token).ConfigureAwait(false));
    }

    [Fact]
    public async Task AFirstEventDeadlineIsNotSatisfiedByCommentsOrPartialBytes()
    {
        await using var prepared = await ResponsesTestAssets.PrepareAsync(true);
        using WaitingStream content = new();
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = new[] { "text/event-stream" } };
        var response = new AdapterUpstreamResponse(200, content, headers, TimeSpan.FromMilliseconds(10));
        var result = await prepared.ParseResponseAsync(response, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_first_byte_timeout", result.Value.ErrorCode);
    }

    private sealed class FailureStream(Exception failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);
    }
    private sealed class WaitingStream : MemoryStream
    {
        private bool _prefix;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_prefix) { _prefix = true; ": comment\n\ndata: {"u8.CopyTo(buffer.Span); return ": comment\n\ndata: {"u8.Length; }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
