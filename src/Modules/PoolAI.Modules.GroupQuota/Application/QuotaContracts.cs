#pragma warning disable MA0048 // The small quota-period control-plane contract is intentionally collocated.
using System.Numerics;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

public sealed record GetGroupQuotaQuery(
    GroupActor Actor,
    EntityId GroupId);

public enum QuotaMutationOperation
{
    AdjustTotal,
    ResetPeriod,
}

public sealed record AuthorizeQuotaMutationCommand(
    EntityId RequestId,
    GroupActor Actor,
    EntityId GroupId,
    QuotaMutationOperation Operation,
    string? IpAddress,
    string? UserAgent);

public sealed record AdjustGroupQuotaCommand(
    EntityId RequestId,
    GroupActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    long ExpectedVersion,
    long NewTotalTokens,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record ResetGroupQuotaCommand(
    EntityId RequestId,
    GroupActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    long ExpectedVersion,
    long TotalTokens,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record GroupQuotaView(
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

public sealed record GroupQuotaCommandOutcome(
    int StatusCode,
    bool IsReplay,
    GroupQuotaView Value,
    string ETag);
#pragma warning restore MA0048
