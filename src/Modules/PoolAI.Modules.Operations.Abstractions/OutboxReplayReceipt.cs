namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxReplayReceipt(EntityId MessageId, long EventSequence);
