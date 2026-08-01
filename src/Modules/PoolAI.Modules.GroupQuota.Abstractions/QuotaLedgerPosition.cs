namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record QuotaLedgerPosition(
    EntityId GroupId,
    EntityId PeriodId,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens);
