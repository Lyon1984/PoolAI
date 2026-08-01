namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record AttemptSettlementFact(
    EntityId AttemptId,
    EntityId RequestId,
    int AttemptIndex,
    EntityId ReservationId,
    EntityId GroupId,
    EntityId PeriodId,
    EntityId AccountId,
    EntityId ChannelId,
    SettlementProvider Provider,
    string RequestedModel,
    string UpstreamModel,
    UsageAttemptOutcome Outcome,
    int? UpstreamHttpStatus,
    string? ErrorCode,
    bool IsStreaming,
    AttemptUsage Usage,
    AttemptUsageAdjustment? Adjustment,
    DateTimeOffset DispatchStartedAt,
    DateTimeOffset? FirstTokenAt,
    DateTimeOffset CompletedAt);
