using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PoolAI.ArchitectureTests;

public sealed partial class DependencyBoundaryTests
{
    private static readonly string[] ForbiddenArchitectureConstructs =
    [
        "IRepository<",
        "IQueryable<",
        "BuildServiceProvider(",
        "static IServiceProvider",
    ];

    private static readonly string[] DomainForbiddenDependencies =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "StackExchange.Redis",
        "System.Net.Http",
        ".Infrastructure",
        ".Endpoints",
    ];

    private static readonly string[] ApprovedPostgresRuntimeFriends =
    [
        "PoolAI.IntegrationTests",
        "PoolAI.Modules.GroupQuota",
        "PoolAI.Modules.Identity",
        "PoolAI.Modules.Operations",
        "PoolAI.Modules.Usage",
    ];

    private static readonly string[] ApprovedPostgresRuntimePackages =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Npgsql",
    ];

    [Fact]
    public void ProductionSourceHasNoForbiddenScopeOrArchitectureConstructs()
    {
        string sourceRoot = Path.Combine(RepositoryRoot.Find(), "src");
        string[] sourceFiles = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories);

        foreach (string sourceFile in sourceFiles)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.False(
                ContainsUnmarkedForbiddenScope(source),
                $"Unmarked forbidden scope was found in {sourceFile}.");
            foreach (string forbidden in ForbiddenArchitectureConstructs)
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ForbiddenScopeGuardExemptsOnlyItsMarkedLine()
    {
        const string Guarded = "\"Billing\", // poolai-forbidden-scope-guard";
        const string Unmarked = "namespace PoolAI.Billing;";

        Assert.False(ContainsUnmarkedForbiddenScope(Guarded));
        Assert.True(ContainsUnmarkedForbiddenScope($"{Guarded}{Environment.NewLine}{Unmarked}"));
    }

    [Fact]
    public void DomainSourceHasNoFrameworkOrOutwardDependency()
    {
        string modulesRoot = Path.Combine(RepositoryRoot.Find(), "src", "Modules");
        string[] domainFiles = Directory.GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Contains("Domain", StringComparer.Ordinal))
            .ToArray();

        foreach (string domainFile in domainFiles)
        {
            string source = File.ReadAllText(domainFile);
            foreach (string dependency in DomainForbiddenDependencies)
            {
                Assert.DoesNotContain(dependency, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void OrchestrationHasNoDataOrTransportDependency()
    {
        string project = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "PoolAI.Application.Orchestration",
            "PoolAI.Application.Orchestration.csproj");
        XDocument document = XDocument.Load(project, LoadOptions.None);
        string[] packages = document
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.Equal(["Microsoft.Extensions.DependencyInjection.Abstractions"], packages);

        string[] sources = Directory.GetFiles(Path.GetDirectoryName(project)!, "*.cs");
        foreach (string sourceFile in sources)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("DbContext", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            Assert.DoesNotContain("StackExchange.Redis", source, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SqlContractsAreLinkedAndNotCopiedIntoSourceOrTests()
    {
        string root = RepositoryRoot.Find();
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), "*.sql", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "tests"), "*.sql", SearchOption.AllDirectories));

        string project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Database.Migrations",
            "PoolAI.Database.Migrations.csproj"));
        Assert.Contains("../../docs/database/0001_baseline.sql", project, StringComparison.Ordinal);
        Assert.Contains("../../docs/database/0002_quota_functions.sql", project, StringComparison.Ordinal);
        Assert.Contains("../../docs/database/0003_runtime_permissions.sql", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseManifestIsCanonicalAndSharedWithoutHostOrMigratorCoupling()
    {
        string root = RepositoryRoot.Find();
        string manifest = Path.Combine(root, "docs", "release-manifest-v1.json");
        Assert.True(File.Exists(manifest), $"Missing authoritative release manifest: {manifest}");

        foreach (string area in new[] { "src", "tests", "deploy" })
        {
            Assert.Empty(Directory.GetFiles(
                Path.Combine(root, area),
                "release-manifest-v1.json",
                SearchOption.AllDirectories));
        }

        string migrationsProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Database.Migrations",
            "PoolAI.Database.Migrations.csproj"));
        string operationsProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "PoolAI.Modules.Operations.csproj"));
        Assert.Contains("../../docs/release-manifest-v1.json", migrationsProject, StringComparison.Ordinal);
        Assert.Contains("../../../docs/release-manifest-v1.json", operationsProject, StringComparison.Ordinal);

        foreach (string host in new[] { "PoolAI.Api", "PoolAI.Worker" })
        {
            string hostProject = File.ReadAllText(Path.Combine(
                root,
                "src",
                host,
                $"{host}.csproj"));
            Assert.DoesNotContain("PoolAI.Database.Migrations", hostProject, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("PoolAI.Database.Migrations", operationsProject, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerChecksRuntimeDependenciesBeforeEnteringItsRunLoop()
    {
        string worker = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "PoolAI.Worker",
            "Program.cs"));
        int check = worker.IndexOf(".CheckAsync(", StringComparison.Ordinal);
        int run = worker.IndexOf("host.RunAsync(", StringComparison.Ordinal);

        Assert.True(check >= 0, "Worker startup must execute the runtime readiness gate.");
        Assert.True(run > check, "Worker startup must pass readiness before entering its run loop.");
        Assert.Contains("if (!readinessResult.IsReady)", worker, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", worker, StringComparison.Ordinal);
        Assert.Contains("readinessResult.FailureCode", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionalAppendersCannotCommitOrDisposeTheCallingUnitOfWork()
    {
        string root = RepositoryRoot.Find();
        string context = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.BuildingBlocks",
            "IUnitOfWorkContext.cs"));
        Assert.DoesNotContain("CommitAsync", context, StringComparison.Ordinal);
        Assert.DoesNotContain("IAsyncDisposable", context, StringComparison.Ordinal);

        string unitOfWork = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.BuildingBlocks",
            "IUnitOfWork.cs"));
        Assert.DoesNotContain("IUnitOfWorkContext,", unitOfWork, StringComparison.Ordinal);
        Assert.Contains("IUnitOfWorkContext Context { get; }", unitOfWork, StringComparison.Ordinal);

        string factory = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.BuildingBlocks",
            "IUnitOfWorkFactory.cs"));
        Assert.Contains("ValueTask<IUnitOfWork> BeginAsync", factory, StringComparison.Ordinal);

        string modules = Path.Combine(root, "src", "Modules");
        string[] ports =
        [
            Path.Combine("PoolAI.Modules.Operations.Abstractions", "ICommandIdempotencyStore.cs"),
            Path.Combine("PoolAI.Modules.Operations.Abstractions", "IAuditAppender.cs"),
            Path.Combine("PoolAI.Modules.Operations.Abstractions", "IOutboxAppender.cs"),
            Path.Combine("PoolAI.Modules.Operations.Abstractions", "IInboxReceiptAppender.cs"),
            Path.Combine("PoolAI.Modules.Operations.Abstractions", "IOutboxDeliveryStore.cs"),
            Path.Combine("PoolAI.Modules.Identity.Abstractions", "IEmailOutboxDeliveryStore.cs"),
            Path.Combine("PoolAI.Modules.Usage.Abstractions", "IUsageAggregationCheckpoint.cs"),
        ];
        foreach (string port in ports)
        {
            string source = File.ReadAllText(Path.Combine(modules, port));
            Assert.Contains("IUnitOfWorkContext unitOfWorkContext", source, StringComparison.Ordinal);
            Assert.DoesNotContain("IUnitOfWork unitOfWork", source, StringComparison.Ordinal);
        }

        AssertTransactionalAdaptersCannotReachCommitCapabilities(modules);
    }

    [Fact]
    public void VendorSpecificPostgresTypesStayInsideInfrastructureAndCompositionRoots()
    {
        string modulesRoot = Path.Combine(RepositoryRoot.Find(), "src", "Modules");
        string[] forbidden =
        [
            "Npgsql",
            "PoolAI.Infrastructure.Postgres",
            "DbConnection",
            "DbTransaction",
        ];
        string[] protectedFiles = Directory
            .GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string[] segments = path.Split(Path.DirectorySeparatorChar);
                return segments.Any(segment =>
                        segment is "Domain" or "Application" or "Endpoints")
                    || segments.Any(segment => segment.EndsWith(
                        ".Abstractions",
                        StringComparison.Ordinal));
            })
            .ToArray();

        foreach (string protectedFile in protectedFiles)
        {
            string source = File.ReadAllText(protectedFile);
            foreach (string dependency in forbidden)
            {
                Assert.DoesNotContain(dependency, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SharedPostgresRuntimeHasOnlyTheApprovedTechnicalSurface()
    {
        string root = RepositoryRoot.Find();
        string runtimeRoot = Path.Combine(
            root,
            "src",
            "PoolAI.Infrastructure.Postgres");
        string[] sourceFiles = Directory.GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories);
        string source = string.Join(
            Environment.NewLine,
            sourceFiles.Select(File.ReadAllText));

        AssertNoAmbientOrGenericPostgresConstructs(source);
        AssertNoBusinessTablesInSharedPostgresRuntime(source);
        AssertTransactionSessionCannotOwnTransaction(runtimeRoot);
        AssertSessionAdvisoryLockOwnership(root, runtimeRoot);
        AssertApprovedPostgresFriendAssemblies(runtimeRoot);
        AssertApprovedPostgresRuntimePackages(runtimeRoot);
        AssertPostgresRuntimeRegistration(runtimeRoot);
    }

    private static void AssertNoAmbientOrGenericPostgresConstructs(string source)
    {
        string[] forbiddenAmbientOrGenericConstructs =
        [
            "System.Transactions",
            "TransactionScope",
            "Transaction.Current",
            "AsyncLocal<",
            "ThreadStatic",
            "IRepository<",
            "ExecuteAsync<",
            "SqlExecutor",
            "DatabaseExecutor",
        ];
        foreach (string forbidden in forbiddenAmbientOrGenericConstructs)
        {
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        }
    }

    private static void AssertNoBusinessTablesInSharedPostgresRuntime(string source)
    {
        string[] businessTableNames =
        [
            "audit_logs",
            "email_outbox",
            "idempotency_records",
            "inbox_messages",
            "outbox_messages",
            "aggregation_watermarks",
            "group_quota",
            "usage_attempts",
        ];
        foreach (string tableName in businessTableNames)
        {
            Assert.DoesNotContain(tableName, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertTransactionSessionCannotOwnTransaction(string runtimeRoot)
    {
        string session = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "PostgresTransactionSession.cs"));
        Assert.False(
            ExposedPostgresConnectionOrTransactionMember().IsMatch(session),
            "The non-committing transaction context must not expose its raw connection or transaction.");
        Assert.DoesNotContain("CommitAsync", session, StringComparison.Ordinal);
        Assert.DoesNotContain("RollbackAsync", session, StringComparison.Ordinal);
        Assert.DoesNotContain("IAsyncDisposable", session, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeAsync", session, StringComparison.Ordinal);
    }

    private static void AssertTransactionalAdaptersCannotReachCommitCapabilities(string modulesRoot)
    {
        string[] transactionalAdapters = Directory
            .GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsGeneratedPath(path))
            .Where(path => path.Split(Path.DirectorySeparatorChar)
                .Contains("Infrastructure", StringComparer.Ordinal))
            .Where(path =>
            {
                string source = File.ReadAllText(path);
                bool usesPostgresRuntime = source.Contains("Npgsql", StringComparison.Ordinal)
                    || source.Contains("PoolAI.Infrastructure.Postgres", StringComparison.Ordinal);
                return usesPostgresRuntime
                    && source.Contains("IUnitOfWorkContext", StringComparison.Ordinal);
            })
            .ToArray();

        Assert.NotEmpty(transactionalAdapters);
        foreach (string adapter in transactionalAdapters)
        {
            string source = File.ReadAllText(adapter);
            Assert.DoesNotContain("CommitAsync(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RollbackAsync(", source, StringComparison.Ordinal);
            Assert.False(
                RawPostgresConnectionOrTransactionAccess().IsMatch(source),
                $"A module Infrastructure adapter reached a raw PostgreSQL capability: {adapter}");
        }
    }

    private static void AssertSessionAdvisoryLockOwnership(string root, string runtimeRoot)
    {
        string providerPath = Path.Combine(
            runtimeRoot,
            "AdvisoryLocks",
            "PostgresSessionAdvisoryLockProvider.cs");
        string leasePath = Path.Combine(
            runtimeRoot,
            "AdvisoryLocks",
            "PostgresSessionAdvisoryLockLease.cs");
        Assert.True(File.Exists(providerPath));
        Assert.True(File.Exists(leasePath));

        string provider = File.ReadAllText(providerPath);
        string lease = File.ReadAllText(leasePath);
        string technicalLock = string.Concat(provider, Environment.NewLine, lease);
        Assert.Contains(
            "internal sealed class PostgresSessionAdvisoryLockProvider",
            provider,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal sealed class PostgresSessionAdvisoryLockLease",
            lease,
            StringComparison.Ordinal);
        Assert.Contains("long lockId", provider, StringComparison.Ordinal);
        Assert.Contains("OpenConnectionAsync", provider, StringComparison.Ordinal);
        Assert.Contains("pg_try_advisory_lock", provider, StringComparison.Ordinal);
        Assert.Contains("VerifyOwnershipAsync", lease, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_unlock", lease, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkerJob", technicalLock, StringComparison.Ordinal);
        Assert.DoesNotContain("CommitAsync", technicalLock, StringComparison.Ordinal);
        Assert.DoesNotContain("RollbackAsync", technicalLock, StringComparison.Ordinal);
        Assert.DoesNotContain("NpgsqlTransaction", technicalLock, StringComparison.Ordinal);
        Assert.False(
            ExposedPostgresConnectionOrTransactionMember().IsMatch(technicalLock),
            "A session advisory-lock lease must not expose its dedicated connection.");

        string workerAdapter = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Workers",
            "PostgresWorkerSessionLockProvider.cs"));
        Assert.Contains("WorkerSessionLockId.Derive(job)", workerAdapter, StringComparison.Ordinal);
        Assert.Contains("PostgresSessionAdvisoryLockProvider", workerAdapter, StringComparison.Ordinal);
        Assert.Contains("PostgresSessionAdvisoryLockLease", workerAdapter, StringComparison.Ordinal);
        string[] forbiddenMechanics =
        [
            "Npgsql",
            "OpenConnectionAsync",
            "pg_try_advisory_lock",
            "pg_advisory_unlock",
        ];
        foreach (string forbiddenMechanic in forbiddenMechanics)
        {
            Assert.DoesNotContain(forbiddenMechanic, workerAdapter, StringComparison.Ordinal);
        }
    }

    private static void AssertApprovedPostgresFriendAssemblies(string runtimeRoot)
    {
        string assemblyInfo = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "Properties",
            "AssemblyInfo.cs"));
        string[] friends = InternalsVisibleToAssembly()
            .Matches(assemblyInfo)
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ApprovedPostgresRuntimeFriends, friends);
    }

    private static void AssertApprovedPostgresRuntimePackages(string runtimeRoot)
    {
        XDocument project = XDocument.Load(Path.Combine(
            runtimeRoot,
            "PoolAI.Infrastructure.Postgres.csproj"));
        string[] packages = project
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ApprovedPostgresRuntimePackages, packages);
    }

    private static void AssertPostgresRuntimeRegistration(string runtimeRoot)
    {
        string registration = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "DependencyInjection.cs"));
        Assert.Contains(
            "AddPoolAiPostgresRuntime",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<IUnitOfWorkFactory>",
            registration,
            StringComparison.Ordinal);
        Assert.Contains(
            "AddSingleton<PostgresSessionAdvisoryLockProvider>",
            registration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPostgresRuntimeIsUsedOnlyByInfrastructureAndHostCompositionRoots()
    {
        string root = RepositoryRoot.Find();
        string modulesRoot = Path.Combine(root, "src", "Modules");
        string[] moduleSources = Directory
            .GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsGeneratedPath(path))
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Contains("Infrastructure", StringComparer.Ordinal))
            .ToArray();
        foreach (string sourceFile in moduleSources)
        {
            string source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain(
                "PoolAI.Infrastructure.Postgres",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
        }

        foreach (string host in new[] { "PoolAI.Api", "PoolAI.Worker" })
        {
            string hostRoot = Path.Combine(root, "src", host);
            string[] hostSources = Directory
                .GetFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedPath(path))
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "Program.cs",
                    StringComparison.Ordinal))
                .ToArray();
            foreach (string sourceFile in hostSources)
            {
                string source = File.ReadAllText(sourceFile);
                Assert.DoesNotContain(
                    "PoolAI.Infrastructure.Postgres",
                    source,
                    StringComparison.Ordinal);
                Assert.DoesNotContain("Npgsql", source, StringComparison.Ordinal);
            }
        }

        string migratorProject = File.ReadAllText(Path.Combine(
            root,
            "src",
            "PoolAI.Migrator",
            "PoolAI.Migrator.csproj"));
        Assert.DoesNotContain(
            "PoolAI.Infrastructure.Postgres",
            migratorProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoutingCommandCannotExpressCrossGroupFallback()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Routing.Abstractions",
            "RouteAccountCommand.cs"));

        Assert.Contains("EntityId GroupId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuotaGroupId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RoutingGroupId", source, StringComparison.Ordinal);
    }

    [GeneratedRegex(
        "InternalsVisibleTo\\(\\\"(?<assembly>[^\\\"]+)\\\"\\)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex InternalsVisibleToAssembly();

    [GeneratedRegex(
        @"\b(?:Payment|Billing|Pricing|Balance|Refund|Promo|Redeem|Affiliate|Commission)\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CommercialNamespace();

    [GeneratedRegex(
        @"\b(?:public|internal|protected(?:\s+internal)?|private\s+protected)\s+(?:static\s+)?(?:readonly\s+)?Npgsql(?:Connection|Transaction)\s+\w+\s*(?:=>|\{|;|\()",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex ExposedPostgresConnectionOrTransactionMember();

    [GeneratedRegex(
        @"\.(?:Connection|Transaction)\b|\bNpgsql(?:Connection|Transaction)\b",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex RawPostgresConnectionOrTransactionAccess();

    private static bool ContainsUnmarkedForbiddenScope(string source)
    {
        string unguarded = string.Join(
            Environment.NewLine,
            source
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.Contains(
                    "poolai-forbidden-scope-guard",
                    StringComparison.Ordinal)));
        return CommercialNamespace().IsMatch(unguarded);
    }

    private static bool IsGeneratedPath(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin", StringComparer.Ordinal)
            || segments.Contains("obj", StringComparer.Ordinal);
    }
}
