namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Complete transport envelope. EventSequence is the physical Outbox sequence while
/// SourceEventSequence is the stable logical quota-ledger sequence retained by replays.
/// </summary>
public sealed record GroupQuotaEventEnvelopeV1(
    EntityId MessageId,
    string Topic,
    string EventType,
    int SchemaVersion,
    long EventSequence,
    long SourceEventSequence,
    string AggregateType,
    EntityId AggregateId,
    long? AggregateVersion,
    string DeduplicationKey,
    DateTimeOffset OccurredAt,
    EntityId CorrelationId,
    EntityId? CausationId,
    EntityId? ReplayOf,
    GroupQuotaEventV1 Payload);
