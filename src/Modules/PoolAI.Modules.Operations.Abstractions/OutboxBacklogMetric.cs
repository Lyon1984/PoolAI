namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxBacklogMetric(
    string EventType,
    long PendingCount,
    double OldestAgeSeconds);
