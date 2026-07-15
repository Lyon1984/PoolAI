namespace PoolAI.Modules.Operations.Abstractions;

public sealed record IntegrationEvent(
    EntityId MessageId,
    string DeduplicationKey,
    string Topic,
    int SchemaVersion,
    string AggregateType,
    EntityId AggregateId,
    long? AggregateVersion,
    string EventType,
    long? SourceEventSequence,
    EntityId CorrelationId,
    EntityId? CausationId,
    JsonElement Payload,
    DateTimeOffset OccurredAt);
