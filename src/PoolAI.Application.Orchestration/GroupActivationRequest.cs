using PoolAI.BuildingBlocks;

namespace PoolAI.Application.Orchestration;

public sealed record GroupActivationRequest(
    ActorContext Actor,
    EntityId GroupId,
    long ExpectedGroupVersion,
    string IdempotencyKey,
    string Reason);
