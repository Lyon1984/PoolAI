namespace PoolAI.Modules.Supply.Abstractions;

public sealed record SupplyReadinessSnapshot(
    EntityId GroupId,
    bool IsReady,
    string OpaqueToken,
    long ConfigurationVersion,
    DateTimeOffset ObservedAt);
