namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountRouteCapabilities(
    bool Responses,
    bool ChatCompletions,
    bool FunctionTools,
    bool Streaming);
