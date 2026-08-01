namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record SettleReservationCommand(
    DispatchedReservationHandle Reservation,
    UsageAttemptOutcome AttemptOutcome,
    int? UpstreamHttpStatus,
    string? ErrorCode,
    string? UpstreamRequestId,
    DateTimeOffset? FirstTokenAt,
    DateTimeOffset CompletedAt,
    UsageRequestOutcome? RequestOutcome,
    TokenUsage Usage,
    SettlementUsageSource UsageSource,
    JsonElement? RawUpstreamUsage);
