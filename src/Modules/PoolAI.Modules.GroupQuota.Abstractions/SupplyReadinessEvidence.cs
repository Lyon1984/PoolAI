namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record SupplyReadinessEvidence(
    string OpaqueToken,
    DateTimeOffset ObservedAt);
