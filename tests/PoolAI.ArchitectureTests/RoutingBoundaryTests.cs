using System.Xml.Linq;

namespace PoolAI.ArchitectureTests;

public sealed class RoutingBoundaryTests
{
    private static readonly string[] ProductionTextExtensions =
    [
        ".cs",
        ".csproj",
        ".json",
        ".props",
        ".targets",
        ".xml",
        ".yaml",
        ".yml",
    ];

    [Fact]
    public void ProductionRuntimeHasNoUserLeaseKeyOrConfiguration()
    {
        string root = RepositoryRoot.Find();
        string configurationGuardPath = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Configuration",
            "PoolAiRuntimeConfigurationValidator.cs");
        string configurationGuard = File.ReadAllText(configurationGuardPath);

        Assert.Equal(
            1,
            CountOccurrences(configurationGuard, "\"Concurrency:User\""));
        Assert.Contains(
            "key.StartsWith(\"Concurrency:User\", StringComparison.OrdinalIgnoreCase)",
            configurationGuard,
            StringComparison.Ordinal);

        foreach (string productionFile in ProductionTextFiles(Path.Combine(root, "src")))
        {
            string source = File.ReadAllText(productionFile);
            if (string.Equals(
                    productionFile,
                    configurationGuardPath,
                    StringComparison.Ordinal))
            {
                source = source.Replace(
                    "\"Concurrency:User\"",
                    "\"RejectedConfigurationPrefix\"",
                    StringComparison.Ordinal);
            }

            string normalized = string.Concat(
                source.Where(static character => char.IsLetterOrDigit(character)));
            foreach (string forbidden in new[]
            {
                "UserLease",
                "LeaseUser",
                "UserConcurrency",
                "ConcurrencyUser",
            })
            {
                Assert.DoesNotContain(
                    forbidden,
                    normalized,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void RoutingContractsDoNotExposeLeaseOwnerOrToken()
    {
        string abstractionsRoot = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Routing.Abstractions");
        string route = File.ReadAllText(Path.Combine(
            abstractionsRoot,
            "AccountRoute.cs"));
        string router = File.ReadAllText(Path.Combine(
            abstractionsRoot,
            "IAccountRouter.cs"));
        string publicContracts = string.Join(
            Environment.NewLine,
            Directory
                .GetFiles(abstractionsRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText))
            .Replace(
                "CancellationToken",
                "CancellationSignal",
                StringComparison.Ordinal)
            .Replace(
                "cancellationToken",
                "cancellationSignal",
                StringComparison.Ordinal);

        foreach (string forbidden in new[]
        {
            "Owner",
            "Token",
            "Member",
            "Fencing",
            "CoordinationLeaseOwner",
        })
        {
            Assert.DoesNotContain(
                forbidden,
                route,
                StringComparison.OrdinalIgnoreCase);
        }

        foreach (string forbidden in new[]
        {
            "Owner",
            "Token",
            "Member",
            "Fencing",
            "CoordinationLeaseOwner",
        })
        {
            Assert.DoesNotContain(
                forbidden,
                publicContracts,
                StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains(
            "ValueTask<Result<IAccountLease>> RouteAsync(",
            router,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingUsesPortsInsteadOfRedisPostgresOrHttpDependencies()
    {
        string modulesRoot = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules");
        string routingRoot = Path.Combine(
            modulesRoot,
            "PoolAI.Modules.Routing");
        string abstractionsRoot = Path.Combine(
            modulesRoot,
            "PoolAI.Modules.Routing.Abstractions");
        AssertRoutingProjectReferences(routingRoot, abstractionsRoot);

        string source = string.Join(
            Environment.NewLine,
            ProductionTextFiles(routingRoot)
                .Concat(ProductionTextFiles(abstractionsRoot))
                .Select(File.ReadAllText));
        foreach (string forbidden in new[]
        {
            "StackExchange.Redis",
            "IConnectionMultiplexer",
            "ConnectionMultiplexer",
            "RedisKey",
            "RedisValue",
            "RedisResult",
            "Npgsql",
            "PoolAI.Infrastructure.Postgres",
            "Microsoft.EntityFrameworkCore",
            "DbContext",
            "System.Net.Http",
            "HttpClient",
            "HttpRequestMessage",
            "HttpResponseMessage",
            "IHttpClientFactory",
        })
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    private static void AssertRoutingProjectReferences(
        string routingRoot,
        string abstractionsRoot)
    {
        XDocument project = XDocument.Load(Path.Combine(
            routingRoot,
            "PoolAI.Modules.Routing.csproj"));
        string[] projectReferences = ProjectReferences(project);
        Assert.Equal(
            [
                "PoolAI.BuildingBlocks",
                "PoolAI.Modules.Operations.Abstractions",
                "PoolAI.Modules.Routing.Abstractions",
                "PoolAI.Modules.Supply.Abstractions",
            ],
            projectReferences);
        Assert.Equal(
            [
                "Microsoft.Extensions.Configuration.Binder",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Hosting.Abstractions",
            ],
            PackageReferences(project));

        XDocument abstractionsProject = XDocument.Load(Path.Combine(
            abstractionsRoot,
            "PoolAI.Modules.Routing.Abstractions.csproj"));
        Assert.Equal(
            ["PoolAI.BuildingBlocks"],
            ProjectReferences(abstractionsProject));
        Assert.Empty(PackageReferences(abstractionsProject));
    }

    private static string[] ProjectReferences(XDocument project) =>
        project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => Path.GetFileNameWithoutExtension(include!))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] PackageReferences(XDocument project) =>
        project
            .Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => include!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void WorkerLoadsOnlyTheRoutingHealthGraph()
    {
        RoutingHostGraph graph = ReadRoutingHostGraph();
        AssertApiRoutingGraph(graph);
        AssertWorkerRoutingGraph(graph);
        AssertHealthRegistration(graph);
    }

    private static RoutingHostGraph ReadRoutingHostGraph()
    {
        string root = RepositoryRoot.Find();
        string apiProgram = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Api",
            "Program.cs"));
        string apiProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Api",
            "PoolAI.Api.csproj"));
        string workerProgram = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Worker",
            "Program.cs"));
        string workerProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Worker",
            "PoolAI.Worker.csproj"));
        string routingRegistration = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Routing",
            "DependencyInjection.cs"));
        int healthRegistrationStart = routingRegistration.IndexOf(
            "public static IServiceCollection AddRoutingHealthModule(",
            StringComparison.Ordinal);
        Assert.True(healthRegistrationStart >= 0);
        int healthRegistrationEnd = routingRegistration.IndexOf(
            "    private static void AddHealthCore(",
            healthRegistrationStart,
            StringComparison.Ordinal);

        Assert.True(healthRegistrationEnd > healthRegistrationStart);
        string healthRegistration = routingRegistration[
            healthRegistrationStart..healthRegistrationEnd];
        return new(
            apiProgram,
            apiProject,
            workerProgram,
            workerProject,
            routingRegistration,
            healthRegistration);
    }

    private static void AssertApiRoutingGraph(RoutingHostGraph graph)
    {
        Assert.Equal(
            1,
            CountOccurrences(graph.ApiProgram, ".AddRoutingModule()"));
        Assert.DoesNotContain(
            ".AddRoutingHealthModule(",
            graph.ApiProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "PoolAI.Modules.Routing/PoolAI.Modules.Routing.csproj",
            graph.ApiProject.Replace('\\', '/'),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Routing\",",
            graph.RoutingRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "HostCapability.Api | HostCapability.Worker",
            graph.RoutingRegistration,
            StringComparison.Ordinal);
    }

    private static void AssertWorkerRoutingGraph(RoutingHostGraph graph)
    {
        Assert.Equal(
            1,
            CountOccurrences(
                graph.WorkerProgram,
                ".AddRoutingHealthModule("));
        Assert.DoesNotContain(
            ".AddRoutingModule()",
            graph.WorkerProgram,
            StringComparison.Ordinal);
        Assert.Contains(
            "PoolAI.Modules.Routing/PoolAI.Modules.Routing.csproj",
            graph.WorkerProject.Replace('\\', '/'),
            StringComparison.Ordinal);

        string workerComposition = string.Concat(
            graph.WorkerProgram,
            graph.WorkerProject);
        foreach (string forbidden in new[]
        {
            "AccountRouter",
            "IAccountRouter",
            "RouteAffinity",
            "IRouteAffinityStore",
            "GroupRequestRateLimiter",
            "IGroupRequestRateLimiter",
            "PoolAI.Modules.Gateway",
            "PoolAI.Adapters.OpenAI",
            "Endpoints",
        })
        {
            Assert.DoesNotContain(
                forbidden,
                workerComposition,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                forbidden,
                graph.HealthRegistration,
                StringComparison.Ordinal);
        }
    }

    private static void AssertHealthRegistration(RoutingHostGraph graph)
    {
        Assert.Contains(
            "AddHealthCore(services);",
            graph.HealthRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccountHealthWorkerService",
            graph.HealthRegistration,
            StringComparison.Ordinal);
    }

    private static IEnumerable<string> ProductionTextFiles(string root) =>
        Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => !IsGeneratedPath(path))
            .Where(path => ProductionTextExtensions.Contains(
                Path.GetExtension(path),
                StringComparer.OrdinalIgnoreCase));

    private static bool IsGeneratedPath(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.Ordinal)
            || segments.Contains("obj", StringComparer.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(
                   value,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private sealed record RoutingHostGraph(
        string ApiProgram,
        string ApiProject,
        string WorkerProgram,
        string WorkerProject,
        string RoutingRegistration,
        string HealthRegistration);
}
