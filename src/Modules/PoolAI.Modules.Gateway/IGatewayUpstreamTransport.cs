using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal interface IGatewayUpstreamTransport
{
    ValueTask<GatewayUpstreamTransportResult> SendAsync(
        IPreparedUpstreamAttempt preparedAttempt,
        AdapterAttemptContext attemptContext,
        AdapterCapability capability,
        IUpstreamCredentialHandle credential,
        CancellationToken cancellationToken);
}
