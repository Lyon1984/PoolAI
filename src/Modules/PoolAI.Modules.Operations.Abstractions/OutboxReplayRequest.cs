namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxReplayRequest(
    EntityId DeadMessageId,
    EntityId NewMessageId,
    string NewDeduplicationKey);
