namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxTerminalMetric(
    string Topic,
    string EventType,
    string Reason,
    long Count);
