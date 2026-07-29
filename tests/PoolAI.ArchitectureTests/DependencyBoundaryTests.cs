using System.Text.RegularExpressions;
using System.Text.Json;
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
        "PoolAI.Modules.SubscriptionAccess",
        "PoolAI.Modules.Supply",
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
    public void FrontendExposesNoRegistrationRoute()
    {
        string router = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "frontend",
            "src",
            "router",
            "index.ts"));

        Assert.DoesNotContain("register", router, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sign-up", router, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signup", router, StringComparison.OrdinalIgnoreCase);
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
    public void MigratorRuntimeImageDoesNotRequireTheAspNetCoreSharedFramework()
    {
        string projectPath = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "PoolAI.Database.Migrations",
            "PoolAI.Database.Migrations.csproj");
        XDocument project = XDocument.Load(projectPath, LoadOptions.None);
        string[] frameworkReferences = project
            .Descendants("FrameworkReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("Microsoft.AspNetCore.App", frameworkReferences);
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

    [Fact]
    public void SharedSecretsRuntimeIsBclOnlyAndUsedOnlyByOwningInfrastructure()
    {
        string root = RepositoryRoot.Find();
        string runtimeRoot = Path.Combine(
            root,
            "src",
            "PoolAI.Infrastructure.Secrets");
        AssertSecretsRuntimeProjectIsBclOnly(runtimeRoot);
        AssertSecretsRuntimeEffectiveGraphIsBclOnly(runtimeRoot);
        AssertSecretsRuntimeHasNoFrameworkOrBusinessPolicy(runtimeRoot);
        AssertOnlyOwningInfrastructureConsumesSecrets(root);
        AssertHostsDoNotConsumeSecretsDirectly(root);
        AssertSupplyCredentialUsesDisposableLease(root);
    }

    private static void AssertSecretsRuntimeProjectIsBclOnly(string runtimeRoot)
    {
        string project = File.ReadAllText(Path.Combine(
            runtimeRoot,
            "PoolAI.Infrastructure.Secrets.csproj"));
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectReference", project, StringComparison.Ordinal);
    }

    private static void AssertSecretsRuntimeEffectiveGraphIsBclOnly(string runtimeRoot)
    {
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            runtimeRoot,
            "obj",
            "project.assets.json")));
        JsonElement target = assets.RootElement
            .GetProperty("targets")
            .GetProperty("net10.0");
        string[] effectivePackages = target
            .EnumerateObject()
            .Select(static dependency =>
                dependency.Name[..dependency.Name.IndexOf('/')])
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "Meziantou.Analyzer",
                "Microsoft.CodeAnalysis.BannedApiAnalyzers",
            ],
            effectivePackages);
        foreach (JsonProperty dependency in target.EnumerateObject())
        {
            Assert.False(dependency.Value.TryGetProperty("compile", out _));
            Assert.False(dependency.Value.TryGetProperty("runtime", out _));
            Assert.False(dependency.Value.TryGetProperty("native", out _));
        }

        JsonElement projectReferences = assets.RootElement
            .GetProperty("project")
            .GetProperty("restore")
            .GetProperty("frameworks")
            .GetProperty("net10.0")
            .GetProperty("projectReferences");
        Assert.Empty(projectReferences.EnumerateObject());

        string configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!
            .Name;
        using JsonDocument runtimeGraph = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            runtimeRoot,
            "bin",
            configuration,
            "net10.0",
            "PoolAI.Infrastructure.Secrets.deps.json")));
        JsonProperty runtimeLibrary = Assert.Single(runtimeGraph.RootElement
            .GetProperty("libraries")
            .EnumerateObject());
        Assert.StartsWith(
            "PoolAI.Infrastructure.Secrets/",
            runtimeLibrary.Name,
            StringComparison.Ordinal);
        Assert.Equal(
            "project",
            runtimeLibrary.Value.GetProperty("type").GetString());
    }

    private static void AssertSecretsRuntimeHasNoFrameworkOrBusinessPolicy(string runtimeRoot)
    {
        string runtimeSource = string.Join(
            Environment.NewLine,
            Directory
                .GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedBuildPath(path))
                .Select(File.ReadAllText));
        string[] forbiddenRuntimeDependencies =
        [
            "Microsoft.Extensions",
            "Npgsql",
            "EntityFrameworkCore",
            "IConfiguration",
            "ILogger",
            "account-credential",
            "email-delivery-secret",
            "idempotency-response",
            "totp-secret",
        ];
        foreach (string forbidden in forbiddenRuntimeDependencies)
        {
            Assert.DoesNotContain(forbidden, runtimeSource, StringComparison.Ordinal);
        }
    }

    private static void AssertOnlyOwningInfrastructureConsumesSecrets(string root)
    {
        string modulesRoot = Path.Combine(root, "src", "Modules");
        string[] consumers = Directory
            .GetFiles(modulesRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !IsGeneratedBuildPath(path))
            .Where(path => File.ReadAllText(path).Contains(
                "PoolAI.Infrastructure.Secrets",
                StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(consumers);
        foreach (string consumer in consumers)
        {
            string relative = Path.GetRelativePath(modulesRoot, consumer);
            Assert.True(
                relative.StartsWith(
                    "PoolAI.Modules.Identity",
                    StringComparison.Ordinal)
                || relative.StartsWith(
                    "PoolAI.Modules.Supply",
                    StringComparison.Ordinal),
                $"Unexpected shared Secrets consumer: {relative}");
            string declaredNamespace = ReadFileScopedNamespace(
                File.ReadAllText(consumer),
                relative);
            Assert.True(
                declaredNamespace.StartsWith(
                    "PoolAI.Modules.Identity.Infrastructure",
                    StringComparison.Ordinal)
                || declaredNamespace.StartsWith(
                    "PoolAI.Modules.Supply.Infrastructure",
                    StringComparison.Ordinal),
                $"Shared Secrets consumer is outside an owning Infrastructure namespace: "
                + $"{declaredNamespace} ({relative})");
        }
    }

    private static string ReadFileScopedNamespace(string source, string relativePath)
    {
        string? declaration = source
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith(
                "namespace ",
                StringComparison.Ordinal));
        Assert.NotNull(declaration);
        Assert.EndsWith(";", declaration, StringComparison.Ordinal);
        string result = declaration["namespace ".Length..^1].Trim();
        Assert.False(
            string.IsNullOrWhiteSpace(result),
            $"Shared Secrets consumer has no namespace: {relativePath}");
        return result;
    }

    private static void AssertSupplyCredentialUsesDisposableLease(string root)
    {
        string ports = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Supply",
            "Application",
            "Ports");
        string protector = File.ReadAllText(Path.Combine(
            ports,
            "IAccountCredentialProtector.cs"));
        string lease = File.ReadAllText(Path.Combine(
            ports,
            "AccountCredentialLease.cs"));

        Assert.Contains(
            "ValueTask<AccountCredentialLease> UnprotectAsync(",
            protector,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ValueTask<string>",
            protector,
            StringComparison.Ordinal);
        Assert.Contains(
            "AccountCredentialLease : IDisposable",
            lease,
            StringComparison.Ordinal);
        Assert.Contains(
            "CryptographicOperations.ZeroMemory",
            lease,
            StringComparison.Ordinal);
    }

    private static void AssertHostsDoNotConsumeSecretsDirectly(string root)
    {
        foreach (string host in new[] { "PoolAI.Api", "PoolAI.Worker", "PoolAI.Migrator" })
        {
            string hostRoot = Path.Combine(root, "src", host);
            string hostSource = string.Join(
                Environment.NewLine,
                Directory
                    .GetFiles(hostRoot, "*.cs", SearchOption.AllDirectories)
                    .Where(static path => !IsGeneratedBuildPath(path))
                    .Select(File.ReadAllText));
            Assert.DoesNotContain(
                "PoolAI.Infrastructure.Secrets",
                hostSource,
                StringComparison.Ordinal);
        }
    }

    private static bool IsGeneratedBuildPath(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);

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
