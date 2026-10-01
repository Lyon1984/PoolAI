using System.Text.Json;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Api;

internal sealed class ChatHttpOutput(HttpContext context) : IGatewayResponseOutput
{
    private bool _terminal;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (context.RequestAborted.IsCancellationRequested) { return; }
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteEventAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
    {
        if (_terminal || context.RequestAborted.IsCancellationRequested) { return; }
        if (!string.Equals(eventName, "chat.completion.chunk", StringComparison.Ordinal)
            || !string.Equals(payload.GetProperty("object").GetString(), eventName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Chat output event is inconsistent.");
        }
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { context.Abort(); }
    }

    internal async ValueTask WriteDoneAsync(CancellationToken cancellationToken)
    {
        if (_terminal || context.RequestAborted.IsCancellationRequested) { return; }
        _terminal = true;
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            await context.Response.Body.WriteAsync("data: [DONE]\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { context.Abort(); }
    }

    internal async ValueTask WriteErrorAsync(string code, CancellationToken cancellationToken)
    {
        if (_terminal || context.RequestAborted.IsCancellationRequested) { return; }
        _terminal = true;
        string publicCode = code is "upstream_usage_out_of_range" or "upstream_first_byte_timeout"
            or "upstream_stream_idle_timeout" ? code : "upstream_stream_error";
        JsonElement error = JsonSerializer.SerializeToElement(new
        {
            error = new { message = "The upstream stream could not be completed safely.",
                type = "server_error", param = (string?)null, code = publicCode },
        });
        try { await WriteJsonAsync(error, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { context.Abort(); }
    }

    private async ValueTask WriteJsonAsync(JsonElement payload, CancellationToken cancellationToken)
    {
        await context.Response.Body.WriteAsync("data: "u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
        await context.Response.Body.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
