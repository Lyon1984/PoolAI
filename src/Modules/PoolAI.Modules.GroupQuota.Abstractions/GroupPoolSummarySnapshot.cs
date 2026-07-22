namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupPoolSummarySnapshot(
    EntityId GroupId,
    string GroupName,
    GroupLifecycle Lifecycle,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    GroupPoolQuotaStatus QuotaStatus,
    DateTimeOffset UpdatedAt);
