namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record NormalizedGatewayRequest(
    EntityId RequestId,
    string Model,
    bool Stream,
    JsonElement Payload)
{
    public override string ToString() => nameof(NormalizedGatewayRequest);
}
