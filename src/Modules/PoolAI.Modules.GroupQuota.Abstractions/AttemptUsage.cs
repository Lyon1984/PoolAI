namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record AttemptUsage(
    TokenUsage Tokens,
    SettlementUsageSource Source,
    bool IsEstimated);
