namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record AttemptUsageAdjustment(
    EntityId QuotaEventId,
    BigInteger PreviousTotalTokens,
    TokenUsage CorrectedTokens,
    SettlementUsageSource Source,
    BigInteger DeltaTokens,
    DateTimeOffset AdjustedAt);
