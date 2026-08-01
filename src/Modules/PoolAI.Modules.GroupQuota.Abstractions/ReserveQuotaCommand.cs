namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReserveQuotaCommand(
    EntityId RequestId,
    EntityId AttemptId,
    int AttemptIndex,
    EntityId UserId,
    EntityId ApiKeyId,
    EntityId SubscriptionId,
    EntityId GroupId,
    EntityId AccountId,
    EntityId ChannelId,
    UsageRequestEndpoint Endpoint,
    string RequestedModel,
    string? ClientRequestId,
    long EstimatedTokens,
    bool IsStreaming,
    string LeaseOwner);
