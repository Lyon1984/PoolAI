using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Adapters.OpenAI;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.ContractTests;

public sealed class ChatParserBoundaryContractTests
{
    private static readonly string[] EventStreamMediaType = ["text/event-stream"];
    [Theory]
    [InlineData("object", "\"other\"")]
    [InlineData("id", "\"\"")]
    [InlineData("model", "null")]
    [InlineData("created", "\"1\"")]
    [InlineData("choices", "null")]
    [InlineData("choices", "[null]")]
    [InlineData("choices", "[{\"index\":-1,\"message\":{\"role\":\"assistant\",\"content\":\"x\"},\"finish_reason\":\"stop\"}]")]
    [InlineData("choices", "[{\"index\":0,\"message\":{\"role\":\"user\",\"content\":\"x\"},\"finish_reason\":\"stop\"}]")]
    [InlineData("choices", "[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":null},\"finish_reason\":\"stop\"}]")]
    [InlineData("choices", "[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"x\"},\"finish_reason\":1}]")]
    public async Task InvalidSuccessCannotEscapeAsAChatCompletion(string field, string value)
    {
        JsonObject completed = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
        completed[field] = JsonNode.Parse(value);
        var result = await ParseAsync(completed.ToJsonString(), false);
        Assert.True(result.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Error.Code);
    }

    [Theory]
    [InlineData(1, "id", "\"other\"")]
    [InlineData(1, "model", "\"other\"")]
    [InlineData(1, "created", "1783987201")]
    [InlineData(0, "choices", "[]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{\"role\":\"user\"},\"finish_reason\":null}]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{\"content\":1},\"finish_reason\":null}]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{\"refusal\":\"not frozen\"},\"finish_reason\":null}]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{},\"finish_reason\":\"other\"}]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{},\"finish_reason\":0}]")]
    [InlineData(0, "choices", "[{\"index\":0,\"delta\":{},\"finish_reason\":null},{\"index\":0,\"delta\":{},\"finish_reason\":null}]")]
    [InlineData(0, "usage", "{\"prompt_tokens\":8,\"completion_tokens\":2,\"total_tokens\":10}")]
    [InlineData(3, "usage", "null")]
    public async Task StreamIdentityChoiceAndUsageOrderAreValidated(int index, string field, string value)
    {
        string body = ChatAdapterContractTests.MutateFixture("chat-completions-text.sse", index,
            frame => frame[field] = JsonNode.Parse(value));
        var result = await ParseAsync(body, true, true);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_protocol_error", result.Value.ErrorCode);
        Assert.False(result.Value.StreamCompleted);
        Assert.Null(result.Value.TerminalEvent);
    }

    [Theory]
    [InlineData("[{\"index\":0,\"id\":\"\"}]")]
    [InlineData("[{\"index\":0,\"type\":\"other\"}]")]
    [InlineData("[{\"index\":0,\"function\":{}}]")]
    [InlineData("[{\"index\":0,\"function\":{\"arguments\":1}}]")]
    [InlineData("[{\"index\":0,\"function\":{\"name\":\"\"}}]")]
    [InlineData("[{\"index\":0,\"function\":{\"unknown\":\"x\"}}]")]
    [InlineData("[{\"index\":0}]")]
    [InlineData("[{\"index\":0},{\"index\":0}]")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task IncompleteOrMalformedToolCallsCannotProduceDone(string tools)
    {
        string body = ChatAdapterContractTests.MutateFixture("chat-completions-function-call.sse", 1,
            frame => frame["choices"]![0]!["delta"]!["tool_calls"] = JsonNode.Parse(tools));
        var result = await ParseAsync(body, true, true);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_protocol_error", result.Value.ErrorCode);
        Assert.False(result.Value.StreamCompleted);
    }

    [Theory]
    [InlineData("call_weather_01", "get_weather", true)]
    [InlineData("changed", "get_weather", false)]
    [InlineData("call_weather_01", "changed", false)]
    public async Task RepeatedFunctionIdentityIsStableRatherThanReplaced(string id, string name, bool valid)
    {
        string wire = ChatAdapterContractTests.MutateFixture("chat-completions-function-call.sse", 2, frame =>
        {
            JsonNode tool = frame["choices"]![0]!["delta"]!["tool_calls"]![0]!;
            tool["id"] = id;
            tool["type"] = "function";
            tool["function"]!["name"] = name;
        });
        var result = await ParseAsync(wire, true, true);
        Assert.True(result.IsSuccess);
        Assert.Equal(valid, result.Value.StreamCompleted);
        Assert.Equal(valid ? null : "upstream_protocol_error", result.Value.ErrorCode);
    }

    [Fact]
    public async Task MultiDataLinesAreJoinedAndMalformedUtf8IsRejected()
    {
        string wire = ResponsesTestAssets.WireFixture("chat-completions-text.sse").Replace(
            ",\"object\":", ",\ndata: \"object\":", StringComparison.Ordinal);
        var result = await ParseAsync(wire, true, true);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.StreamCompleted);
        await using var prepared = await ChatTestAssets.PrepareAsync(true);
        using MemoryStream invalid = new([0xff, 0x0a, 0x0a]);
        var malformed = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(invalid, true), TestContext.Current.CancellationToken);
        Assert.True(malformed.IsSuccess);
        Assert.Equal("upstream_protocol_error", malformed.Value.ErrorCode);
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("event: chat.completion.chunk\ndata: {}\n\n")]
    [InlineData("id: provider-id\ndata: {}\n\n")]
    [InlineData("data: {}\n")]
    [InlineData("data: {}\n\n")]
    [InlineData("data: \n\n")]
    public async Task MalformedWireCannotGenerateASuccessTerminal(string wire)
    {
        var result = await ParseAsync(wire, true);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_protocol_error", result.Value.ErrorCode);
        Assert.False(result.Value.StreamCompleted);
    }

    [Fact]
    public async Task EofMissingUsageRepeatedUsageAndContentAfterFinishNeverComplete()
    {
        string[] frames = ResponsesTestAssets.WireFixture("chat-completions-text.sse").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (string wire in new[]
        {
            string.Join("\n\n", frames.Take(3)) + "\n\n",
            string.Join("\n\n", frames.Where((_, index) => index != 3)) + "\n\n",
            string.Join("\n\n", frames.Take(4).Append(frames[3]).Append(frames[4])) + "\n\n",
            string.Join("\n\n", frames.Take(3).Append(frames[1]).Concat(frames.Skip(3))) + "\n\n",
        })
        {
            var result = await ParseAsync(wire, true, true);
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value.ErrorCode);
            Assert.False(result.Value.StreamCompleted);
            Assert.Null(result.Value.TerminalEvent);
        }
    }

    [Fact]
    public async Task VendorMetadataIsDroppedAndObfuscationFollowsTheClientOption()
    {
        foreach (bool padding in new[] { false, true })
        {
            JsonObject request = JsonNode.Parse(ChatTestAssets.Request(true, true).GetRawText())!.AsObject();
            request["stream_options"]!["include_obfuscation"] = padding;
            ChatTestAssets.RecordingOutput output = new();
            await using var prepared = await ChatTestAssets.PrepareAsync(true, true, output, payload: JsonSerializer.SerializeToElement(request));
            string wire = ChatAdapterContractTests.MutateFixture("chat-completions-text.sse", 0, frame =>
            {
                frame["obfuscation"] = "padding";
                frame["system_fingerprint"] = "vendor-internal";
                frame["choices"]![0]!["logprobs"] = null;
            });
            using var content = new ResponsesTestAssets.ChunkedStream(wire.Replace("\n", "\r\n", StringComparison.Ordinal), 1);
            var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
            Assert.Null(result.Value.ErrorCode);
            Assert.Equal(padding, output.Events[0].TryGetProperty("obfuscation", out _));
            Assert.False(output.Events[0].TryGetProperty("system_fingerprint", out _));
            Assert.False(output.Events[0].GetProperty("choices")[0].TryGetProperty("logprobs", out _));
        }
    }

    [Theory]
    [InlineData("chat-completions-error.sse")]
    [InlineData("chat-completions-usage-out-of-range.sse")]
    public async Task CanonicalProviderErrorsAreNotForwardedVerbatim(string fixture)
    {
        ChatTestAssets.RecordingOutput output = new();
        await using var prepared = await ChatTestAssets.PrepareAsync(true, output: output);
        using var content = new ResponsesTestAssets.ChunkedStream(ResponsesTestAssets.WireFixture(fixture), 1);
        var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, true), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_stream_error", result.Value.ErrorCode);
        Assert.False(result.Value.StreamCompleted);
        Assert.All(output.Events, frame => Assert.False(frame.TryGetProperty("error", out _)));
    }

    [Fact]
    public async Task CacheReasoningAndPredictionDetailsAreLosslessAndNotInvented()
    {
        JsonObject body = JsonNode.Parse(ChatTestAssets.Completion().GetRawText())!.AsObject();
        body["usage"]!["prompt_tokens_details"] = JsonNode.Parse("{\"cached_tokens\":2,\"cache_write_tokens\":3}");
        body["usage"]!["completion_tokens_details"] = JsonNode.Parse("{\"reasoning_tokens\":1,\"accepted_prediction_tokens\":0,\"rejected_prediction_tokens\":0}");
        var result = await ParseAsync(body.ToJsonString(), false);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Usage!.CacheReadTokens);
        Assert.Equal(3, result.Value.Usage.CacheCreationTokens);
        Assert.Equal(1, result.Value.Usage.ThinkingTokens);
        body["usage"]!["completion_tokens_details"]!["accepted_prediction_tokens"] = JsonNode.Parse("9007199254740992");
        var unsafeResult = await ParseAsync(body.ToJsonString(), false);
        Assert.True(unsafeResult.IsSuccess);
        Assert.Equal("upstream_usage_out_of_range", unsafeResult.Value.ErrorCode);
    }

    [Fact]
    public async Task ParserBoundsReadFaultsAndCancellationAreExplicit()
    {
        foreach ((bool stream, string wire) in new[] { (false, new string(' ', 16 * 1024 * 1024 + 1)),
            (true, "data: " + new string('x', 1024 * 1024 + 1)) })
        {
            var result = await ParseAsync(wire, stream);
            Assert.Equal("upstream_protocol_error", result.IsFailure ? result.Error.Code : result.Value.ErrorCode);
        }
        foreach (bool stream in new[] { true, false })
        {
            foreach (string code in new[] { "upstream_first_byte_timeout", "upstream_stream_idle_timeout", "io" })
            {
                await using var prepared = await ChatTestAssets.PrepareAsync(stream);
                using FailureStream content = new(code is "io" ? new IOException() : new UpstreamReadTimeoutException(code));
                var result = await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, stream), TestContext.Current.CancellationToken);
                Assert.True(result.IsSuccess);
                Assert.Equal(code is "io" ? stream ? "upstream_stream_error" : "upstream_dispatch_ambiguous" : code, result.Value.ErrorCode);
            }
        }
        await using var cancelled = await ChatTestAssets.PrepareAsync(false);
        using MemoryStream empty = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.ParseResponseAsync(ResponsesTestAssets.Response(empty, false), cancellation.Token).ConfigureAwait(false));
    }

    [Fact]
    public async Task CommentsAndPartialBytesDoNotSatisfyTheFirstValidChunkDeadline()
    {
        await using var prepared = await ChatTestAssets.PrepareAsync(true);
        using WaitingStream content = new();
        AdapterUpstreamResponse response = new(200, content,
            new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = EventStreamMediaType },
            TimeSpan.FromMilliseconds(10));
        var result = await prepared.ParseResponseAsync(response, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal("upstream_first_byte_timeout", result.Value.ErrorCode);
    }

    [Fact]
    public async Task CapabilityMismatchAndDisposedPreparationCannotSend()
    {
        ChatUpstreamAdapter adapter = new(OpenAiCapabilityDescriptor.R1Capabilities[3]);
        var mismatch = await adapter.PrepareAsync(ResponsesTestAssets.Context(),
            new NormalizedGatewayRequest(EntityId.New(), "client-alias", false, ChatTestAssets.Request()), TestContext.Current.CancellationToken);
        Assert.True(mismatch.IsFailure);
        await using var prepared = await ChatTestAssets.PrepareAsync(false);
        await prepared.DisposeAsync();
        Assert.True((await prepared.CreateRequestAsync(TestContext.Current.CancellationToken)).IsFailure);
    }

    private static async ValueTask<Result<NormalizedUpstreamResult>> ParseAsync(string wire, bool stream, bool includeUsage = false)
    {
        var prepared = await ChatTestAssets.PrepareAsync(stream, includeUsage).ConfigureAwait(false);
        await using (prepared.ConfigureAwait(false))
        {
            using var content = new ResponsesTestAssets.ChunkedStream(wire, 4096);
            return await prepared.ParseResponseAsync(ResponsesTestAssets.Response(content, stream), TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
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
