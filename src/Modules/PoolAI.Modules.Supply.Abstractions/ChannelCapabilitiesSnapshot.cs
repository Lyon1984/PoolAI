namespace PoolAI.Modules.Supply.Abstractions;

public sealed record ChannelCapabilitiesSnapshot(
    bool Responses,
    bool ChatCompletions,
    bool FunctionTools,
    bool Streaming);
