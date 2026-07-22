namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupActivationOrchestrationCommand(
    ActorContext Actor,
    EntityId GroupId,
    long ExpectedGroupVersion,
    string IdempotencyKey,
    string Reason,
    GroupMetadataPatch? MetadataPatch = null,
    EntityId? RequestId = null,
    string? IpAddress = null,
    string? UserAgent = null);
