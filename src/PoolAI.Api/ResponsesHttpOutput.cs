using System.Text;
using System.Text.Json;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Api;

internal sealed class ResponsesHttpOutput(HttpContext context) : IGatewayResponseOutput
{
    private readonly HttpContext _context = context;
    private int _sequence;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_context.RequestAborted.IsCancellationRequested) { return; }
        if (!_context.Response.HasStarted)
        {
            _context.Response.StatusCode = StatusCodes.Status200OK;
            _context.Response.ContentType = "text/event-stream";
            _context.Response.Headers.CacheControl = "no-cache, no-transform";
            await _context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask WriteEventAsync(string eventName, JsonElement payload, CancellationToken cancellationToken)
    {
        // A disconnected client no longer owns output, but the upstream reader
        // must remain available to the independently bounded settlement drain.
        if (_context.RequestAborted.IsCancellationRequested) { return; }
        if (!string.Equals(payload.GetProperty("type").GetString(), eventName, StringComparison.Ordinal)
            || payload.GetProperty("sequence_number").GetInt32() != _sequence)
        {
            throw new InvalidOperationException("The Gateway output event sequence is inconsistent.");
        }

        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            byte[] prefix = Encoding.UTF8.GetBytes("event: " + eventName + "\ndata: ");
            await _context.Response.Body.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(_context.Response.Body, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _context.Response.Body.WriteAsync("\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            _sequence++;
        }
        catch (OperationCanceledException) when (_context.RequestAborted.IsCancellationRequested) { }
        catch (IOException) { _context.Abort(); }
    }

    internal ValueTask WriteErrorAsync(string code, CancellationToken cancellationToken)
    {
        string publicCode = code is "upstream_usage_out_of_range" or "upstream_first_byte_timeout"
            or "upstream_stream_idle_timeout" ? code : "upstream_stream_error";
        JsonElement terminal = JsonSerializer.SerializeToElement(new
        {
            type = "error", code = publicCode, message = "The upstream stream could not be completed safely.",
            param = (string?)null, sequence_number = _sequence,
        });
        return WriteEventAsync("error", terminal, cancellationToken);
    }
}
