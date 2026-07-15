namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record AdapterAttemptContext(
    EntityId RequestId,
    EntityId AttemptId,
    int AttemptIndex,
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId);
