namespace PoolAI.Database.Migrations;

public sealed record MigrationAsset(
    long Version,
    string Name,
    string ChecksumSha256,
    string Sql);
