namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CommandIdempotencyAcquireResult(
    CommandIdempotencyDisposition Disposition,
    CommandIdempotencyLease? Lease,
    CommandIdempotencyResponse? Response)
{
    public static CommandIdempotencyAcquireResult Acquired(CommandIdempotencyLease lease) =>
        new(CommandIdempotencyDisposition.Acquired, lease, null);

    public static CommandIdempotencyAcquireResult Replay(CommandIdempotencyResponse response) =>
        new(CommandIdempotencyDisposition.Replay, null, response);

    public static CommandIdempotencyAcquireResult Conflict { get; } =
        new(CommandIdempotencyDisposition.Conflict, null, null);

    public static CommandIdempotencyAcquireResult Busy { get; } =
        new(CommandIdempotencyDisposition.Busy, null, null);
}
