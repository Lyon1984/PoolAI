using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace PoolAI.EndToEndTests;

internal sealed class LoopbackResponsesUpstream : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<Reply> _replies = Channel.CreateUnbounded<Reply>();
    private readonly Task _serve;
    private readonly Lock _gate = new();
    private readonly List<JsonElement> _requests = [];

    internal LoopbackResponsesUpstream()
    {
        _listener.Start();
        BaseAddress = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _serve = ServeAsync(_shutdown.Token);
    }

    internal string BaseAddress { get; }
    internal JsonElement[] Requests { get { lock (_gate) { return [.. _requests]; } } }
    internal void Enqueue(string body, bool stream, int status = 200) =>
        Assert.True(_replies.Writer.TryWrite(new Reply(body, stream, status)));

    internal ReplyPause EnqueuePausedBeforeCompletion(string body)
    {
        int terminal = body.IndexOf("event: response.completed", StringComparison.Ordinal);
        Assert.True(terminal > 0);
        ReplyPause pause = new(Encoding.UTF8.GetByteCount(body.AsSpan(0, terminal)));
        Assert.True(_replies.Writer.TryWrite(new Reply(body, true, 200, pause)));
        return pause;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try { await _serve.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
        finally { _shutdown.Dispose(); }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using TcpClient connection = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await HandleAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask HandleAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using NetworkStream network = connection.GetStream();
        using StreamReader reader = new(network, Encoding.UTF8, false, 1024, leaveOpen: true);
        string requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        int length = 0;
        bool authenticated = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } header)
        {
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
            }
            if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                authenticated = header[14..].Trim().Equals("Bearer m2-exit-upstream-credential", StringComparison.Ordinal);
            }
        }

        Assert.True(authenticated, "Transport must attach only the fixture credential.");
        if (requestLine.StartsWith("GET /models ", StringComparison.Ordinal))
        {
            await WriteAsync(network, new Reply("{\"data\":[]}", false, 200), cancellationToken).ConfigureAwait(false);
            return;
        }

        Assert.StartsWith("POST /responses ", requestLine, StringComparison.Ordinal);
        char[] body = new char[length];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await reader.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            Assert.True(read > 0);
            offset += read;
        }
        using JsonDocument document = JsonDocument.Parse(new string(body));
        lock (_gate) { _requests.Add(document.RootElement.Clone()); }
        Reply reply = await _replies.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(network, reply, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask WriteAsync(NetworkStream network, Reply reply, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(reply.Body);
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Status} Fixture\r\n"
            + $"Content-Type: {(reply.Stream ? "text/event-stream" : "application/json")}\r\n"
            + $"Content-Length: {body.Length}\r\nx-request-id: req_m4_e2\r\nConnection: close\r\n\r\n");
        await network.WriteAsync(headers, cancellationToken).ConfigureAwait(false);
        // Deliberately split UTF-8 and JSON tokens, rather than a line-at-a-time mock.
        for (int index = 0; index < body.Length;)
        {
            if (reply.Pause is { } pause && index == pause.ByteOffset)
            {
                await network.FlushAsync(cancellationToken).ConfigureAwait(false);
                pause.Reached.TrySetResult();
                await pause.Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            int count = Math.Min(7, body.Length - index);
            if (reply.Pause is { } pending && index < pending.ByteOffset)
            {
                count = Math.Min(count, pending.ByteOffset - index);
            }
            await network.WriteAsync(body.AsMemory(index, count), cancellationToken).ConfigureAwait(false);
            index += count;
        }
        await network.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed class ReplyPause(int byteOffset)
    {
        internal int ByteOffset { get; } = byteOffset;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Resume() => Continue.TrySetResult();
    }

    private sealed record Reply(string Body, bool Stream, int Status, ReplyPause? Pause = null);
}
