namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReserveQuotaCommand(
    EntityId RequestId,
    EntityId AttemptId,
    EntityId GroupId,
    EntityId AccountId,
    BigInteger EstimatedTokens,
    bool IsStream);
