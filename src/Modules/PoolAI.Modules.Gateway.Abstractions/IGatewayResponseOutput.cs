namespace PoolAI.Modules.Gateway.Abstractions;

/// <summary>Transport-neutral, backpressured output for validated protocol events.</summary>
public interface IGatewayResponseOutput
{
    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask WriteEventAsync(
        string eventName,
        JsonElement payload,
        CancellationToken cancellationToken);
}
