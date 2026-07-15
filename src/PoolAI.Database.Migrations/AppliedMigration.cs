namespace PoolAI.Database.Migrations;

public sealed record AppliedMigration(long Version, string Name, string ChecksumSha256);
