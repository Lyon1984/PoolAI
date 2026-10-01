using System.Text.Json;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayAttemptOutput(
    IGatewayResponseOutput output,
    IGatewayAttemptOutputEvidenceSink evidence) : IGatewayResponseOutput
{
    private readonly IGatewayResponseOutput _output = output;
    private readonly IGatewayAttemptOutputEvidenceSink _evidence = evidence;
    private bool _started;
    private bool _businessOutput;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (!_started)
        {
            await _output.StartAsync(cancellationToken).ConfigureAwait(false);
            _evidence.MarkDownstreamHeadersCommitted();
            _started = true;
        }
    }

    public async ValueTask WriteEventAsync(
        string eventName,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        // Fence before the write: a partially written frame is never replayable.
        if (!_businessOutput)
        {
            _evidence.MarkBusinessOutputStarted();
            _businessOutput = true;
        }

        await _output.WriteEventAsync(eventName, payload, cancellationToken)
            .ConfigureAwait(false);
    }
}
