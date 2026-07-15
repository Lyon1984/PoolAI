namespace PoolAI.ContractTests;

public sealed class CanonicalContractLocationTests
{
    [Fact]
    public void OpenApiAndFixturesExistOnlyInTheDocumentedContractArea()
    {
        string root = FindRepositoryRoot();
        string openApi = Path.Combine(root, "docs", "contracts", "openapi-v1.yaml");
        string fixtures = Path.Combine(root, "docs", "contracts", "fixtures");

        Assert.True(File.Exists(openApi), $"Missing authoritative contract: {openApi}");
        Assert.NotEmpty(Directory.GetFiles(fixtures, "*", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), "openapi-v1.yaml", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "tests"), "openapi-v1.yaml", SearchOption.AllDirectories));
    }

    [Fact]
    public void ReleaseManifestExistsOnlyInTheDocumentedContractArea()
    {
        string root = FindRepositoryRoot();
        string manifest = Path.Combine(root, "docs", "release-manifest-v1.json");

        Assert.True(File.Exists(manifest), $"Missing authoritative release manifest: {manifest}");
        Assert.Empty(Directory.GetFiles(
            Path.Combine(root, "src"),
            "release-manifest-v1.json",
            SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(root, "tests"),
            "release-manifest-v1.json",
            SearchOption.AllDirectories));
    }

    private static string FindRepositoryRoot()
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
