using System.Security.Cryptography;
using PoolAI.Database.Migrations;

namespace PoolAI.IntegrationTests;

public sealed class MigrationCatalogTests
{
    [Fact]
    public async Task EmbeddedMigrationsMatchTheAuthoritativeSqlBytes()
    {
        string root = FindRepositoryRoot();
        MigrationCatalog catalog = await MigrationCatalog
            .LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L],
            catalog.Assets.Select(asset => asset.Version));
        foreach (MigrationAsset asset in catalog.Assets)
        {
            string path = Path.Combine(root, "docs", "database", asset.Name);
            byte[] source = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            string checksum = Convert.ToHexStringLower(SHA256.HashData(source));

            Assert.Equal(checksum, asset.ChecksumSha256);
            Assert.Equal(File.ReadAllText(path), asset.Sql);
        }
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PoolAI repository root.");
    }
}
