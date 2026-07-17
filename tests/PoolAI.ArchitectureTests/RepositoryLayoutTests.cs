using System.Xml.Linq;

namespace PoolAI.ArchitectureTests;

public sealed class RepositoryLayoutTests
{
    [Fact]
    public void SolutionHasTheFrozenProjectCount()
    {
        string root = RepositoryRoot.Find();
        string[] productionProjects = Directory.GetFiles(
            Path.Combine(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories);
        string[] testProjects = Directory.GetFiles(
            Path.Combine(root, "tests"),
            "*.csproj",
            SearchOption.AllDirectories);

        Assert.Equal(25, productionProjects.Length);
        Assert.Equal(6, testProjects.Length);
    }

    [Fact]
    public void ProductionProjectReferencesMatchTheFrozenDag()
    {
        string root = RepositoryRoot.Find();
        Dictionary<string, string[]> expected = FrozenProjectReferences;
        string[] projects = Directory.GetFiles(
            Path.Combine(root, "src"),
            "*.csproj",
            SearchOption.AllDirectories);

        Dictionary<string, string[]> actual = projects.ToDictionary(
            ProjectName,
            ReadProjectReferences,
            StringComparer.Ordinal);

        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string project, string[] expectedReferences) in expected)
        {
            Assert.Equal(
                expectedReferences.Order(StringComparer.Ordinal),
                actual[project].Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ProjectsUseCentralPackageVersionsOnly()
    {
        string root = RepositoryRoot.Find();
        string[] projects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories);

        foreach (string project in projects)
        {
            XDocument document = XDocument.Load(project, LoadOptions.None);
            IEnumerable<XElement> versionedReferences = document
                .Descendants("PackageReference")
                .Where(reference => reference.Attribute("Version") is not null);
            Assert.Empty(versionedReferences);
        }
    }

    private static string ProjectName(string projectPath) =>
        Path.GetFileNameWithoutExtension(projectPath);

    private static string[] ReadProjectReferences(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath, LoadOptions.None);
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        return document
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFullPath(include!, projectDirectory))
            .Select(ProjectName)
            .ToArray();
    }

    private static readonly Dictionary<string, string[]> FrozenProjectReferences =
        new(StringComparer.Ordinal)
        {
            ["PoolAI.Api"] =
            [
                "PoolAI.Contracts",
                "PoolAI.Application.Orchestration",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.Identity",
                "PoolAI.Modules.SubscriptionAccess",
                "PoolAI.Modules.GroupQuota",
                "PoolAI.Modules.Supply",
                "PoolAI.Modules.Routing",
                "PoolAI.Modules.Usage",
                "PoolAI.Modules.Operations",
                "PoolAI.Modules.Gateway",
                "PoolAI.Adapters.OpenAI",
            ],
            ["PoolAI.Worker"] =
            [
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.Identity",
                "PoolAI.Modules.GroupQuota",
                "PoolAI.Modules.Supply",
                "PoolAI.Modules.Usage",
                "PoolAI.Modules.Operations",
            ],
            ["PoolAI.Migrator"] = ["PoolAI.Database.Migrations"],
            ["PoolAI.Application.Orchestration"] =
            [
                "PoolAI.Modules.Identity.Abstractions",
                "PoolAI.Modules.SubscriptionAccess.Abstractions",
                "PoolAI.Modules.GroupQuota.Abstractions",
                "PoolAI.Modules.Supply.Abstractions",
            ],
            ["PoolAI.Contracts"] = [],
            ["PoolAI.BuildingBlocks"] = [],
            ["PoolAI.Infrastructure.Postgres"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Database.Migrations"] = [],
            ["PoolAI.Modules.Identity.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.SubscriptionAccess.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.GroupQuota.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Supply.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Routing.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Usage.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Operations.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Gateway.Abstractions"] = ["PoolAI.BuildingBlocks"],
            ["PoolAI.Modules.Identity"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.Identity.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
            ],
            ["PoolAI.Modules.SubscriptionAccess"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.SubscriptionAccess.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.Identity.Abstractions",
                "PoolAI.Modules.GroupQuota.Abstractions",
            ],
            ["PoolAI.Modules.GroupQuota"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.GroupQuota.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
            ],
            ["PoolAI.Modules.Supply"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Modules.Supply.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
            ],
            ["PoolAI.Modules.Routing"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Modules.Routing.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.Supply.Abstractions",
            ],
            ["PoolAI.Modules.Usage"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.Usage.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.GroupQuota.Abstractions",
            ],
            ["PoolAI.Modules.Operations"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Infrastructure.Postgres",
                "PoolAI.Modules.Operations.Abstractions",
            ],
            ["PoolAI.Modules.Gateway"] =
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Contracts",
                "PoolAI.Modules.Gateway.Abstractions",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.Identity.Abstractions",
                "PoolAI.Modules.SubscriptionAccess.Abstractions",
                "PoolAI.Modules.GroupQuota.Abstractions",
                "PoolAI.Modules.Supply.Abstractions",
                "PoolAI.Modules.Routing.Abstractions",
                "PoolAI.Modules.Usage.Abstractions",
            ],
            ["PoolAI.Adapters.OpenAI"] = ["PoolAI.Modules.Gateway.Abstractions"],
        };
}
