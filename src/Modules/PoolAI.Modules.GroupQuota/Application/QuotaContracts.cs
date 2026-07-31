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

public enum QuotaMutationIdempotencyKeyStatus
{
    Missing,
    Invalid,
    Multiple,
    Valid,
}

public sealed class QuotaMutationIdempotencyKeyAuditInput
{
    private QuotaMutationIdempotencyKeyAuditInput(
        QuotaMutationIdempotencyKeyStatus status,
        string? validValue)
    {
        Status = status;
        ValidValue = validValue;
    }

    public QuotaMutationIdempotencyKeyStatus Status { get; }

    public string? ValidValue { get; }

    public static QuotaMutationIdempotencyKeyAuditInput Missing { get; } =
        new(QuotaMutationIdempotencyKeyStatus.Missing, validValue: null);

    public static QuotaMutationIdempotencyKeyAuditInput Multiple { get; } =
        new(QuotaMutationIdempotencyKeyStatus.Multiple, validValue: null);

    public static QuotaMutationIdempotencyKeyAuditInput FromSingle(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Missing;
        }

        return value is { Length: >= 1 and <= 128 }
            && value.All(static character => character is >= (char)0x21 and <= (char)0x7e)
                ? new QuotaMutationIdempotencyKeyAuditInput(
                    QuotaMutationIdempotencyKeyStatus.Valid,
                    value)
                : new QuotaMutationIdempotencyKeyAuditInput(
                    QuotaMutationIdempotencyKeyStatus.Invalid,
                    validValue: null);
    }
}

public sealed record AuthorizeQuotaMutationCommand(
    EntityId RequestId,
    GroupActor Actor,
    EntityId GroupId,
    QuotaMutationOperation Operation,
    QuotaMutationIdempotencyKeyAuditInput IdempotencyKeyAudit,
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
