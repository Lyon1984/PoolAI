namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyRequest(
    string Scope,
    string Key,
    EntityId RecordId,
    string ActorFingerprint,
    ReadOnlyMemory<byte> RequestHash,
    EntityId Owner,
    TimeSpan LeaseDuration,
    TimeSpan Retention);
