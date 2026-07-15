namespace PoolAI.Modules.Identity.Abstractions;

public sealed record EmailOutboxDeliveryLease(
    EntityId EmailId,
    EntityId Owner,
    long Generation,
    int Attempt);
