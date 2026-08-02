namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Narrow, immutable view of one append-only GroupQuota ledger event.
/// </summary>
public sealed record GroupQuotaEventFactSnapshot(
    EntityId EventId,
    long SourceEventSequence,
    EntityId CorrelationId,
    EntityId? CausationId,
    EntityId GroupId,
    EntityId PeriodId,
    EntityId? ReservationId,
    EntityId? AttemptId,
    string EventType,
    BigInteger DeltaTotalTokens,
    BigInteger DeltaConsumedTokens,
    BigInteger DeltaReservedTokens,
    BigInteger TotalTokens,
    BigInteger ConsumedTokens,
    BigInteger ReservedTokens,
    DateTimeOffset OccurredAt,
    JsonElement Metadata);
