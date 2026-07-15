namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public sealed record ChangeSubscriptionCommand(
    EntityId SubscriptionId,
    long ExpectedVersion,
    string IdempotencyKey,
    string Reason);
