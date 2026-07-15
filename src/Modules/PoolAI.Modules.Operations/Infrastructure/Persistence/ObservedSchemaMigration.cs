namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal sealed record ObservedSchemaMigration(
    long Version,
    string Name,
    string ChecksumSha256);
