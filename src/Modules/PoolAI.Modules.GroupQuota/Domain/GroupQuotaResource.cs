using System.Numerics;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Domain;

internal sealed record GroupQuotaResource(
    EntityId GroupId,
    EntityId PeriodId,
    GroupPoolQuotaStatus Status,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    BigInteger RemainingTokens,
    BigInteger OverageTokens,
    DateTimeOffset PeriodStartedAt,
    DateTimeOffset? PeriodEndedAt,
    long Version,
    DateTimeOffset UpdatedAt);
