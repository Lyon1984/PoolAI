namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ActivateGroupCommand(
    ActorContext Actor,
    EntityId GroupId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Reason,
    SupplyReadinessEvidence SupplyEvidence);
