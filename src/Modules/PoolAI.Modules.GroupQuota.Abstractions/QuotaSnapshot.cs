namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record QuotaSnapshot(
    EntityId GroupId,
    EntityId PeriodId,
    BigInteger Total,
    BigInteger Consumed,
    BigInteger Reserved,
    long Version);
