namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyLease(
    string Scope,
    string Key,
    EntityId Owner,
    long Generation,
    long Version);
