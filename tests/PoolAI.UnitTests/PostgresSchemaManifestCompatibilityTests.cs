using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class PostgresSchemaManifestCompatibilityTests
{
    [Fact]
    public void ExactManifestHistoryIsCompatible()
    {
        ReleaseManifestPostgresV1 manifest = CreateManifest(minimumCompatibleVersion: 3);

        Assert.True(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            CreateObservedHistory(3)));
    }

    [Fact]
    public void SupportedManifestPrefixIsCompatible()
    {
        ReleaseManifestPostgresV1 manifest = CreateManifest(minimumCompatibleVersion: 2);

        Assert.True(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            CreateObservedHistory(2)));
    }

    [Fact]
    public void EmptyHistoryAndVersionsOutsideTheCompatibilityWindowAreRejected()
    {
        ReleaseManifestPostgresV1 manifest = CreateManifest(minimumCompatibleVersion: 2);

        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            Array.Empty<ObservedSchemaMigration>()));
        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            CreateObservedHistory(1)));
        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            [.. CreateObservedHistory(3), new(4, "0004_future.sql", Hash('4'))]));
    }

    [Fact]
    public void VersionNameAndChecksumDriftAreRejected()
    {
        ReleaseManifestPostgresV1 manifest = CreateManifest(minimumCompatibleVersion: 3);
        ObservedSchemaMigration[] expected = CreateObservedHistory(3);

        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            [expected[0], new(9, expected[1].Name, expected[1].ChecksumSha256), expected[2]]));
        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            [expected[0], new(expected[1].Version, "0002_renamed.sql", expected[1].ChecksumSha256), expected[2]]));
        Assert.False(PostgresSchemaManifestCompatibility.IsCompatible(
            manifest,
            [expected[0], new(expected[1].Version, expected[1].Name, Hash('9')), expected[2]]));
    }

    private static ReleaseManifestPostgresV1 CreateManifest(long minimumCompatibleVersion)
    {
        return new ReleaseManifestPostgresV1(
            18,
            minimumCompatibleVersion,
            3,
            [
                new(1, "0001_baseline.sql", "docs/database/0001_baseline.sql", Hash('1')),
                new(2, "0002_quota_functions.sql", "docs/database/0002_quota_functions.sql", Hash('2')),
                new(3, "0003_runtime_permissions.sql", "docs/database/0003_runtime_permissions.sql", Hash('3')),
            ]);
    }

    private static ObservedSchemaMigration[] CreateObservedHistory(int count)
    {
        ObservedSchemaMigration[] history =
        [
            new(1, "0001_baseline.sql", Hash('1')),
            new(2, "0002_quota_functions.sql", Hash('2')),
            new(3, "0003_runtime_permissions.sql", Hash('3')),
        ];
        return history[..count];
    }

    private static string Hash(char value) => new(value, 64);
}
