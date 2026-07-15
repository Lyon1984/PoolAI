using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal static class PostgresSchemaManifestCompatibility
{
    public static bool IsCompatible(
        ReleaseManifestPostgresV1 expected,
        IReadOnlyList<ObservedSchemaMigration> observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        if (observed.Count == 0 || observed.Count > expected.Migrations.Count)
        {
            return false;
        }

        long currentVersion = observed[^1].Version;
        if (currentVersion < expected.MinimumCompatibleVersion
            || currentVersion > expected.MaximumCompatibleVersion)
        {
            return false;
        }

        for (int index = 0; index < observed.Count; index++)
        {
            ObservedSchemaMigration actual = observed[index];
            ReleaseManifestPostgresMigrationV1 required = expected.Migrations[index];
            if (actual.Version != required.Version
                || !string.Equals(actual.Name, required.Name, StringComparison.Ordinal)
                || !string.Equals(
                    actual.ChecksumSha256,
                    required.Sha256,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
