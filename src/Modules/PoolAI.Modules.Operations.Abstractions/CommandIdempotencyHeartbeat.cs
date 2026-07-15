namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyHeartbeat(
    CommandIdempotencyLease Lease,
    TimeSpan LeaseDuration);
