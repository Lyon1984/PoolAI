namespace PoolAI.Modules.Operations.Abstractions;

public sealed record OutboxDeliveryLease(
    EntityId MessageId,
    EntityId Owner,
    long Generation,
    int Attempt);
