namespace PoolAI.Modules.Gateway.Abstractions;

public interface IUpstreamAdapter
{
    AdapterCapability Capability { get; }

    ValueTask<Result<IPreparedUpstreamAttempt>> PrepareAsync(
        AdapterAttemptContext attempt,
        NormalizedGatewayRequest request,
        CancellationToken cancellationToken);
}
