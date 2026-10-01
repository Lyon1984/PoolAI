extern alias PoolAiApi;

using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ChatOutput = PoolAiApi::PoolAI.Api.ChatHttpOutput;

namespace PoolAI.EndToEndTests;

public sealed class ChatHttpOutputTests
{
    [Fact]
    public async Task ChatWritesDataOnlyAndOneDoneAfterTheLastChunk()
    {
        using MemoryStream body = new();
        DefaultHttpContext context = new();
        context.Response.Body = body;
        ChatOutput output = new(context);
        await output.StartAsync(TestContext.Current.CancellationToken);
        await output.WriteEventAsync("chat.completion.chunk", Chunk(), TestContext.Current.CancellationToken);
        await output.WriteDoneAsync(TestContext.Current.CancellationToken);
        await output.WriteDoneAsync(TestContext.Current.CancellationToken);
        await output.WriteErrorAsync("upstream_stream_error", TestContext.Current.CancellationToken);
        await output.WriteEventAsync("chat.completion.chunk", Chunk(), TestContext.Current.CancellationToken);
        string wire = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache, no-transform", context.Response.Headers.CacheControl);
        Assert.StartsWith("data: {", wire, StringComparison.Ordinal);
        Assert.EndsWith("\n\ndata: [DONE]\n\n", wire, StringComparison.Ordinal);
        Assert.Equal(2, wire.Split("[DONE]", StringSplitOptions.None).Length);
        Assert.DoesNotContain("event:", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", wire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("upstream_usage_out_of_range", "upstream_usage_out_of_range")]
    [InlineData("upstream_first_byte_timeout", "upstream_first_byte_timeout")]
    [InlineData("upstream_stream_idle_timeout", "upstream_stream_idle_timeout")]
    [InlineData("provider-private-message", "upstream_stream_error")]
    public async Task ChatTerminalErrorUsesOnlyTheFrozenErrorObject(string failure, string expected)
    {
        using MemoryStream body = new();
        DefaultHttpContext context = new();
        context.Response.Body = body;
        ChatOutput output = new(context);
        await output.StartAsync(TestContext.Current.CancellationToken);
        await output.WriteErrorAsync(failure, TestContext.Current.CancellationToken);
        await output.WriteErrorAsync(failure, TestContext.Current.CancellationToken);
        await output.WriteDoneAsync(TestContext.Current.CancellationToken);
        string wire = Encoding.UTF8.GetString(body.ToArray());
        using JsonDocument json = JsonDocument.Parse(wire[6..].Trim());
        Assert.Single(json.RootElement.EnumerateObject());
        JsonElement error = json.RootElement.GetProperty("error");
        Assert.Equal(4, error.EnumerateObject().Count());
        Assert.Equal(expected, error.GetProperty("code").GetString());
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
        Assert.DoesNotContain("[DONE]", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-private-message", wire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("response.completed", "chat.completion.chunk")]
    [InlineData("chat.completion.chunk", "chat.completion")]
    public async Task TypedResponsesAndInconsistentPayloadsCannotEnterChat(string eventName, string kind)
    {
        DefaultHttpContext context = new();
        ChatOutput output = new(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => output.WriteEventAsync(eventName,
            JsonSerializer.SerializeToElement(new { @object = kind }), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task DisconnectedClientsReceiveNeitherContentErrorsNorDone()
    {
        using CancellationTokenSource disconnected = new();
        disconnected.Cancel();
        using MemoryStream body = new();
        DefaultHttpContext context = new() { RequestAborted = disconnected.Token };
        context.Response.Body = body;
        ChatOutput output = new(context);
        await output.StartAsync(TestContext.Current.CancellationToken);
        await output.WriteEventAsync("chat.completion.chunk", Chunk(), TestContext.Current.CancellationToken);
        await output.WriteErrorAsync("upstream_stream_error", TestContext.Current.CancellationToken);
        await output.WriteDoneAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData("chunk", false)]
    [InlineData("error", false)]
    [InlineData("done", false)]
    [InlineData("chunk", true)]
    [InlineData("error", true)]
    [InlineData("done", true)]
    public async Task MidWriteIoOrClientCancellationDoesNotEscapeTheProjection(string operation, bool cancellation)
    {
        using CancellationTokenSource abort = new();
        using BrokenStream body = new(abort, cancellation);
        DefaultHttpContext context = new() { RequestAborted = abort.Token };
        context.Response.Body = body;
        ChatOutput output = new(context);
        await output.StartAsync(TestContext.Current.CancellationToken);
        if (string.Equals(operation, "chunk", StringComparison.Ordinal)) { await output.WriteEventAsync("chat.completion.chunk", Chunk(), TestContext.Current.CancellationToken); }
        else if (string.Equals(operation, "done", StringComparison.Ordinal)) { await output.WriteDoneAsync(TestContext.Current.CancellationToken); }
        else { await output.WriteErrorAsync("upstream_stream_error", TestContext.Current.CancellationToken); }
        Assert.Equal(0, body.Length);
    }

    private static JsonElement Chunk() => JsonSerializer.SerializeToElement(new { @object = "chat.completion.chunk" });
    private sealed class BrokenStream(CancellationTokenSource abort, bool cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!cancellation) { return ValueTask.FromException(new IOException("The fixture write failed.")); }
            abort.Cancel();
            return ValueTask.FromCanceled(abort.Token);
        }
    }
}
