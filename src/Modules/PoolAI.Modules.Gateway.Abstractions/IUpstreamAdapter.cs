namespace PoolAI.Modules.Gateway.Abstractions;

public interface IUpstreamAdapter
{
    AdapterCapability Capability { get; }

    ValueTask<Result<NormalizedUpstreamResult>> SendAsync(
        AdapterAttemptContext attempt,
        NormalizedGatewayRequest request,
        CancellationToken cancellationToken);
}
