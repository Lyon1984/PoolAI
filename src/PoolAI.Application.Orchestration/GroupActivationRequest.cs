using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed record GroupActivationRequest(
    ActorContext Actor,
    EntityId GroupId,
    long ExpectedGroupVersion,
    string IdempotencyKey,
    string Reason,
    GroupMetadataPatch? MetadataPatch = null,
    EntityId? RequestId = null,
    string? IpAddress = null,
    string? UserAgent = null);
