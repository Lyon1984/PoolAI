namespace PoolAI.ArchitectureTests;

public sealed class GatewayCredentialBoundaryTests
{
    [Fact]
    public void AdapterAttemptContextSourceHasNoPublicEvidenceMutator()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Gateway.Abstractions",
            "AdapterAttemptContext.cs"));

        Assert.DoesNotContain("public void Mark", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "public AdapterAttemptContext(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("internal AdapterAttemptContext(", source,
            StringComparison.Ordinal);
        Assert.Contains("internal void MarkDispatchedAfterFence", source,
            StringComparison.Ordinal);
        Assert.Contains("internal void MarkRequestBytesWritten", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MarkDownstreamHeadersCommitted() =>", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("MarkBusinessOutputStarted() =>", source,
            StringComparison.Ordinal);
        Assert.Contains("OutputEvidenceSink", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayRequestProcessIsTheOnlyPublicAttemptExecutionBoundary()
    {
        string root = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Modules",
            "PoolAI.Modules.Gateway");
        Dictionary<string, string> expectedDeclarations = new(
            StringComparer.Ordinal)
        {
            ["GatewayCanonicalAccess.cs"] =
                "internal sealed class GatewayCanonicalAccess",
            ["GatewayCanonicalAdmissionService.cs"] =
                "internal sealed class GatewayCanonicalAdmissionService",
            ["GatewaySingleAttemptRequest.cs"] =
                "internal sealed class GatewaySingleAttemptRequest",
            ["GatewaySingleAttemptProcessManager.cs"] =
                "internal sealed class GatewaySingleAttemptProcessManager",
            ["GatewaySingleAttemptExecutor.cs"] =
                "internal sealed class GatewaySingleAttemptExecutor",
            ["IGatewaySingleAttemptExecutor.cs"] =
                "internal interface IGatewaySingleAttemptExecutor",
            ["IGatewayUpstreamTransport.cs"] =
                "internal interface IGatewayUpstreamTransport",
            ["GatewayCredentialHandoff.cs"] =
                "internal sealed class GatewayCredentialHandoff",
        };

        foreach ((string fileName, string declaration) in expectedDeclarations)
        {
            string source = File.ReadAllText(Path.Combine(root, fileName));
            Assert.Contains(declaration, source, StringComparison.Ordinal);
        }

        string requestProcess = File.ReadAllText(Path.Combine(
            root,
            "GatewayRequestProcess.cs"));
        string dependencyInjection = File.ReadAllText(Path.Combine(
            Directory.GetParent(root)!.FullName,
            "PoolAI.Modules.Gateway",
            "DependencyInjection.cs"));
        Assert.Contains(
            "public sealed class GatewayRequestProcess",
            requestProcess,
            StringComparison.Ordinal);
        Assert.Contains(
            "services.AddSingleton(CreateRequestProcess)",
            dependencyInjection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "services.AddSingleton<GatewaySingleAttemptProcessManager>",
            dependencyInjection,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayLifecycleOwnsAttemptResourcesAndFinalEvidence()
    {
        string repositoryRoot = RepositoryRoot.Find();
        string gatewayRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway");
        string lifecycle = File.ReadAllText(Path.Combine(
            gatewayRoot,
            "GatewayAttemptLifecycle.cs"));

        Assert.Contains("internal sealed class GatewayAttemptLifecycle", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("internal EntityId QuotaGroupId", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("internal EntityId RoutingGroupId", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("internal IAccountLease AccountLease", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("internal ReservationHandle? Reservation", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("internal GatewayAttemptEvidence Evidence", lifecycle,
            StringComparison.Ordinal);
        Assert.Contains("FinalDisposition", lifecycle, StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayOutputEvidenceSinkIsNarrowAndAdapterInaccessible()
    {
        string repositoryRoot = RepositoryRoot.Find();
        string gatewayRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway");
        string otherGatewaySources = string.Join(
            Environment.NewLine,
            Directory.GetFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedBuildPath(path))
                .Where(static path => !string.Equals(
                    Path.GetFileName(path),
                    "GatewayAttemptLifecycle.cs",
                    StringComparison.Ordinal))
                .Select(File.ReadAllText));
        string sink = File.ReadAllText(Path.Combine(
            string.Concat(gatewayRoot, ".Abstractions"),
            "IGatewayAttemptOutputEvidenceSink.cs"));
        string adapters = string.Join(
            Environment.NewLine,
            Directory.GetFiles(
                    Path.Combine(repositoryRoot, "src", "Adapters"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedBuildPath(path))
                .Select(File.ReadAllText));

        Assert.Contains(
            "internal interface IGatewayAttemptOutputEvidenceSink",
            sink,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".AdvanceToDownstreamHeadersCommitted()",
            otherGatewaySources,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".AdvanceToBusinessOutputStarted()",
            otherGatewaySources,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IGatewayAttemptOutputEvidenceSink",
            adapters,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterCredentialContractIsAnOpaqueMarkerWithNoSecretCallback()
    {
        string root = RepositoryRoot.Find();
        string abstractions = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway.Abstractions");
        string handle = File.ReadAllText(Path.Combine(
            abstractions,
            "IUpstreamCredentialHandle.cs"));
        string preparedAttempt = File.ReadAllText(Path.Combine(
            abstractions,
            "IPreparedUpstreamAttempt.cs"));

        Assert.Contains(
            "IUpstreamCredentialHandle : IDisposable",
            handle,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Apply", handle, StringComparison.Ordinal);
        Assert.DoesNotContain("delegate", handle, StringComparison.Ordinal);
        Assert.DoesNotContain(" string ", handle, StringComparison.Ordinal);
        Assert.DoesNotContain("byte[]", handle, StringComparison.Ordinal);
        Assert.DoesNotContain("Span<", handle, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IUpstreamCredentialHandle",
            preparedAttempt,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreparedUpstreamRequest",
            preparedAttempt,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdapterUpstreamResponse",
            preparedAttempt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayCredentialBridgeUsesOnlyAbstractionPortsAndContainsNoCredentialSql()
    {
        string root = RepositoryRoot.Find();
        string gatewayRoot = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway");
        string bridge = File.ReadAllText(Path.Combine(
            gatewayRoot,
            "GatewayCredentialHandoff.cs"));
        string gatewaySource = string.Join(
            Environment.NewLine,
            Directory.GetFiles(gatewayRoot, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedBuildPath(path))
                .Select(File.ReadAllText));

        Assert.Contains(
            "IRouteCredentialLeaseSource",
            bridge,
            StringComparison.Ordinal);
        Assert.Contains(
            "IUpstreamCredentialHandle",
            bridge,
            StringComparison.Ordinal);
        foreach (string forbidden in new[]
        {
            "Npgsql",
            "PoolAI.Infrastructure.Secrets",
            "credential_envelope",
        })
        {
            Assert.DoesNotContain(forbidden, gatewaySource, StringComparison.Ordinal);
        }

        Assert.Contains(
            "ITransportCredentialHandle",
            bridge,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GatewayTransportOwnsCredentialAttachmentAndDispatchEvidence()
    {
        string root = RepositoryRoot.Find();
        string gatewayRoot = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway");
        string transport = File.ReadAllText(Path.Combine(
            gatewayRoot,
            "GatewayOutboundTransport.cs"));
        string operation = File.ReadAllText(Path.Combine(
            gatewayRoot,
            "GatewayUpstreamAttemptOperation.cs"));
        string preparedRequest = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway.Abstractions",
            "PreparedUpstreamHeader.cs"));

        Assert.Contains("PlaintextStreamFilter", transport, StringComparison.Ordinal);
        Assert.Contains("AttachAuthorizationOnce", transport, StringComparison.Ordinal);
        Assert.Contains("UseProxy = false", transport, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", transport, StringComparison.Ordinal);
        Assert.Contains("PooledConnectionLifetime = TimeSpan.Zero", transport,
            StringComparison.Ordinal);
        Assert.Contains("GatewayRequestWriteEvidence", transport,
            StringComparison.Ordinal);
        Assert.Contains("ConfirmedNoExecution", operation,
            StringComparison.Ordinal);
        Assert.Contains("CanProveNoRequestBytesWritten", operation,
            StringComparison.Ordinal);
        Assert.Contains("\"authorization\"", preparedRequest,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostsDoNotSubscribeToAuthorityBearingNativeHttpMetrics()
    {
        string root = RepositoryRoot.Find();
        foreach (string host in new[] { "PoolAI.Api", "PoolAI.Worker" })
        {
            string source = File.ReadAllText(Path.Combine(
                root,
                "src",
                host,
                "Observability.cs"));
            int metricsStart = source.IndexOf(
                ".WithMetrics(",
                StringComparison.Ordinal);
            int tracingStart = source.IndexOf(
                ".WithTracing(",
                StringComparison.Ordinal);
            Assert.True(metricsStart >= 0 && tracingStart > metricsStart);
            string metrics = source[metricsStart..tracingStart];
            string tracing = source[tracingStart..];

            Assert.DoesNotContain(
                ".AddHttpClientInstrumentation()",
                metrics,
                StringComparison.Ordinal);
            Assert.Contains(
                ".AddHttpClientInstrumentation()",
                tracing,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AdapterProjectsCannotReferenceSupplyOrSecrets()
    {
        string adaptersRoot = Path.Combine(
            RepositoryRoot.Find(),
            "src",
            "Adapters");
        string source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(adaptersRoot, "*", SearchOption.AllDirectories)
                .Where(static path => !IsGeneratedBuildPath(path))
                .Where(static path => Path.GetExtension(path) is ".cs" or ".csproj")
                .Select(File.ReadAllText));

        Assert.Contains("PoolAI.Modules.Gateway.Abstractions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PoolAI.Modules.Supply", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PoolAI.Infrastructure.Secrets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IUpstreamCredentialHandle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RouteCredentialReader", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpMessageInvoker", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SocketsHttpHandler", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TcpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Net.Sockets", source, StringComparison.Ordinal);
    }

    private static bool IsGeneratedBuildPath(string path) =>
        path.Contains(
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal)
        || path.Contains(
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            StringComparison.Ordinal);
}
