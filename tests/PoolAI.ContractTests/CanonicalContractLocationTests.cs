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
    public void CompatibilityGovernanceRegistriesExistOnlyInTheDocumentedContractArea()
    {
        string root = FindRepositoryRoot();
        string[] fileNames = ["compatibility-resets-v1.json", "compatibility-windows-v1.json"];

        foreach (string fileName in fileNames)
        {
            string registry = Path.Combine(root, "docs", "contracts", fileName);
            Assert.True(File.Exists(registry), $"Missing authoritative contract registry: {registry}");
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(registry));
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), fileName, SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "tests"), fileName, SearchOption.AllDirectories));
        }
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

    [Fact]
    public void IdentityEventSchemaExistsOnlyInTheDocumentedContractAreaAndIsValidJson()
    {
        string root = FindRepositoryRoot();
        string fileName = "identity-events-v1.json";
        string schema = Path.Combine(root, "docs", "contracts", fileName);

        Assert.True(File.Exists(schema), $"Missing authoritative contract: {schema}");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(schema));
        Assert.Equal(
            "poolai.identity.v1",
            document.RootElement.GetProperty("x-poolai-topic").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("x-poolai-schema-version").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), fileName, SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "tests"), fileName, SearchOption.AllDirectories));
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
