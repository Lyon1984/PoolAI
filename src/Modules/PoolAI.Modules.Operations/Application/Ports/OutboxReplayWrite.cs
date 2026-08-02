namespace PoolAI.Modules.Operations.Application.Ports;

internal sealed record OutboxReplayWrite(
    EntityId SourceMessageId,
    EntityId NewMessageId,
    string NewDeduplicationKey);
