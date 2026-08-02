namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxObservabilitySnapshot(
    IReadOnlyList<OutboxBacklogMetric> Backlog,
    IReadOnlyList<OutboxTerminalMetric> Dead,
    IReadOnlyList<OutboxTerminalMetric> Replays)
{
    public static OutboxObservabilitySnapshot Empty { get; } = new([], [], []);
}
