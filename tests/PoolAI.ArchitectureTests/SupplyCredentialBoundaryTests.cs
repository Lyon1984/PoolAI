using System.Text.RegularExpressions;

namespace PoolAI.ArchitectureTests;

public sealed partial class SupplyCredentialBoundaryTests
{
    private const string SupplyModulePrefix =
        "src/Modules/PoolAI.Modules.Supply/";
    private const string SupplyPersistencePrefix =
        "src/Modules/PoolAI.Modules.Supply/Infrastructure/Persistence/";

    [Fact]
    public void AccountCredentialSqlIsOwnedOnlyBySupplyPersistence()
    {
        string root = RepositoryRoot.Find();
        string[] credentialSqlFiles = SourceFiles(Path.Combine(root, "src"))
            .Where(path => ContainsCredentialSql(File.ReadAllText(path)))
            .ToArray();

        Assert.NotEmpty(credentialSqlFiles);
        foreach (string sourceFile in credentialSqlFiles)
        {
            string relative = RelativePath(root, sourceFile);
            Assert.StartsWith(
                SupplyPersistencePrefix,
                relative,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SupplyPostgresFriendAndNpgsqlUsageStayInfrastructureOnly()
    {
        string root = RepositoryRoot.Find();
        string assemblyInfo = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Infrastructure.Postgres",
            "Properties",
            "AssemblyInfo.cs"));
        string[] supplyFriends = InternalsVisibleToAssembly()
            .Matches(assemblyInfo)
            .Select(match => match.Groups["assembly"].Value)
            .Where(static assembly => string.Equals(
                assembly,
                "PoolAI.Modules.Supply",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["PoolAI.Modules.Supply"], supplyFriends);

        string supplyRoot = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Supply");
        string[] npgsqlConsumers = SourceFiles(supplyRoot)
            .Where(path => File.ReadAllText(path).Contains(
                "Npgsql",
                StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(npgsqlConsumers);
        foreach (string consumer in npgsqlConsumers)
        {
            string relative = RelativePath(root, consumer);
            Assert.StartsWith(
                SupplyPersistencePrefix,
                relative,
                StringComparison.Ordinal);
            Assert.Contains(
                "namespace PoolAI.Modules.Supply.Infrastructure",
                File.ReadAllText(consumer),
                StringComparison.Ordinal);
        }

        foreach (string sourceFile in SourceFiles(supplyRoot)
            .Where(path => !RelativePath(root, path).StartsWith(
                $"{SupplyModulePrefix}Infrastructure/",
                StringComparison.Ordinal)))
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "PoolAI.Infrastructure.Postgres",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OperationsAndHostsDoNotOwnCredentialSqlOrSecretRuntime()
    {
        string root = RepositoryRoot.Find();
        foreach (string operationsProject in new[]
        {
            "PoolAI.Modules.Operations",
            "PoolAI.Modules.Operations.Abstractions",
        })
        {
            string operationsRoot = Path.Combine(
                root,
                "src",
                "Modules",
                operationsProject);
            foreach (string sourceFile in SourceFiles(operationsRoot))
            {
                string source = File.ReadAllText(sourceFile);
                Assert.False(
                    ContainsCredentialSql(source),
                    "Operations must not own Account credential SQL: "
                    + RelativePath(root, sourceFile));
            }
        }

        foreach (string host in new[] { "PoolAI.Api", "PoolAI.Worker", "PoolAI.Migrator" })
        {
            string hostRoot = Path.Combine(root, "src", host);
            foreach (string sourceFile in SourceFiles(hostRoot))
            {
                string source = File.ReadAllText(sourceFile);
                Assert.DoesNotContain(
                    "PoolAI.Infrastructure.Secrets",
                    source,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "IAccountCredentialProtector",
                    source,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "SecretEnvelopeV1",
                    source,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AccountCredentialRewrapWorkerIdentityIsStableAndVersioned()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Operations.Abstractions",
            "WorkerJobs.cs"));

        Assert.Contains(
            "public static WorkerJobIdentity AccountCredentialRewrap",
            source,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            Regex.Count(
                source,
                Regex.Escape("poolai:r1:worker:account-credential-rewrap:v1"),
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));
    }

    private static bool ContainsCredentialSql(string source) =>
        (source.Contains("credential_envelope", StringComparison.OrdinalIgnoreCase)
            || source.Contains(
                "poolai_account_credential",
                StringComparison.OrdinalIgnoreCase))
        && (source.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
            || source.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
            || source.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            || source.Contains("DELETE", StringComparison.OrdinalIgnoreCase));

    private static string[] SourceFiles(string root) =>
        Directory
            .GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .ToArray();

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(
            Path.DirectorySeparatorChar,
            '/');

    [GeneratedRegex(
        """InternalsVisibleTo\("(?<assembly>[^"]+)"\)""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex InternalsVisibleToAssembly();
}
