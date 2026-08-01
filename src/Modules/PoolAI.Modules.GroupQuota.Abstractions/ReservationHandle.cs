namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReservationHandle(
    EntityId ReservationId,
    EntityId RequestId,
    EntityId AttemptId,
    int AttemptIndex,
    EntityId GroupId,
    EntityId PeriodId,
    EntityId AccountId,
    EntityId ChannelId,
    long EstimatedTokens,
    bool IsStreaming,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset MaxExpiresAt);
