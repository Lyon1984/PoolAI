namespace PoolAI.Modules.Operations.Application.Ports;

internal enum OutboxReplayPersistenceDisposition
{
    Created,
    Replayed,
    SourceNotFound,
    SourceNotDead,
    ReplayConflict,
    ValidationFailed,
}
