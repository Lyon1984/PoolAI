namespace PoolAI.Modules.Gateway.Abstractions;

public interface IProtocolAdapter
{
    AdapterCapability Capability { get; }

    ValueTask<Result<NormalizedGatewayRequest>> NormalizeAsync(
        JsonElement request,
        CancellationToken cancellationToken);
}
