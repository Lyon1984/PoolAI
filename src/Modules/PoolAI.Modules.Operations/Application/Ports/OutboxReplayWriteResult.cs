namespace PoolAI.Modules.Operations.Application.Ports;

internal sealed record OutboxReplayWriteResult(
    OutboxReplayPersistenceDisposition Disposition,
    EntityId? MessageId = null,
    long? EventSequence = null);
