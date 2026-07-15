namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record AttemptSettlementFact(
    EntityId AttemptId,
    EntityId RequestId,
    EntityId GroupId,
    EntityId AccountId,
    BigInteger InputTokens,
    BigInteger OutputTokens,
    string UsageSource,
    DateTimeOffset CompletedAt);
