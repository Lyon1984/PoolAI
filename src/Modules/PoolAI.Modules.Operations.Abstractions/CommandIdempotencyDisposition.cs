namespace PoolAI.Modules.Operations.Abstractions;

public enum CommandIdempotencyDisposition
{
    Acquired,
    Replay,
    Conflict,
    Busy,
}
