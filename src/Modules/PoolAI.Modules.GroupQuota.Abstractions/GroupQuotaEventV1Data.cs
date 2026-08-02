namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record GroupQuotaEventV1Data(
    EntityId EventId,
    long SourceEventSequence,
    EntityId CorrelationId,
    EntityId? CausationId,
    EntityId GroupId,
    EntityId PeriodId,
    EntityId? ReservationId,
    EntityId? AttemptId,
    BigInteger DeltaTotalTokens,
    BigInteger DeltaConsumedTokens,
    BigInteger DeltaReservedTokens,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    DateTimeOffset OccurredAt,
    JsonElement Metadata);
