namespace PoolAI.ArchitectureTests;

public sealed class OperationsOutboxPublisherBoundaryTests
{
    [Fact]
    public void GenericOutboxPublisherIsRegisteredOnlyByTheWorkerHost()
    {
        string root = RepositoryRoot.Find();
        string apiProgram = Read(root, "src", "PoolAI.Api", "Program.cs");
        string workerProgram = Read(root, "src", "PoolAI.Worker", "Program.cs");
        string moduleRegistration = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "DependencyInjection.cs");
        string workerRegistration = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "OutboxWorkerDependencyInjection.cs");

        Assert.DoesNotContain(
            ".AddOperationsOutboxPublisher(",
            apiProgram,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            CountOccurrences(
                workerProgram,
                ".AddOperationsOutboxPublisher(builder.Configuration)"));
        Assert.DoesNotContain("OutboxPublisherProcessor", moduleRegistration, StringComparison.Ordinal);
        Assert.DoesNotContain("OutboxPublisherService", moduleRegistration, StringComparison.Ordinal);
        Assert.DoesNotContain("IHostedService", moduleRegistration, StringComparison.Ordinal);
        Assert.Contains(
            "TryAddSingleton<OutboxPublisherProcessor>()",
            workerRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServiceDescriptor.Singleton<IHostedService, OutboxPublisherService>()",
            workerRegistration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GenericPublisherDoesNotReferenceConsumerModuleImplementations()
    {
        string root = RepositoryRoot.Find();
        string project = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "PoolAI.Modules.Operations.csproj");
        string operationsRoot = Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations");

        Assert.DoesNotContain("PoolAI.Modules.Usage", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PoolAI.Modules.GroupQuota", project, StringComparison.Ordinal);
        string objectDirectorySegment =
            Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
        foreach (string path in Directory.GetFiles(
                     operationsRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (path.Contains(
                    objectDirectorySegment,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string source = File.ReadAllText(path);
            Assert.DoesNotContain("PoolAI.Modules.Usage", source, StringComparison.Ordinal);
            Assert.DoesNotContain("PoolAI.Modules.GroupQuota", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PublisherClaimAndMetricsKeepTheFrozenContractSurface()
    {
        string root = RepositoryRoot.Find();
        string store = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Persistence",
            "PostgresOutboxDeliveryStore.cs");
        string metrics = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Observability",
            "OutboxPublisherMetrics.cs");

        Assert.Equal(2, CountOccurrences(store, "message.topic = ANY($1)"));
        Assert.Contains("message.source_event_sequence", store, StringComparison.Ordinal);
        Assert.Contains("lineage.is_complete", store, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF message SKIP LOCKED", store, StringComparison.Ordinal);
        Assert.Contains("poolai_outbox_pending", metrics, StringComparison.Ordinal);
        Assert.Contains("poolai_outbox_oldest_age_seconds", metrics, StringComparison.Ordinal);
        Assert.Contains("poolai_outbox_dead_total", metrics, StringComparison.Ordinal);
        Assert.Contains("poolai_outbox_replay_total", metrics, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherSignalsShareOneFrozenLowCardinalityClassifier()
    {
        string root = RepositoryRoot.Find();
        string classifier = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "OutboxTelemetryClassifier.cs");
        string processor = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Worker",
            "OutboxPublisherProcessor.cs");
        string logger = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "LoggingOperationalEventWriter.cs");
        string metrics = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Observability",
            "OutboxPublisherMetrics.cs");
        string store = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Operations",
            "Infrastructure",
            "Persistence",
            "PostgresOutboxObservabilityStore.cs");

        Assert.Contains("FrozenSet<string>", classifier, StringComparison.Ordinal);
        Assert.Contains("NormalizeOperationalPayload", classifier, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier.NormalizeTopic", processor, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier.NormalizeEventType", processor, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier.NormalizeReason", processor, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier", logger, StringComparison.Ordinal);
        Assert.Contains("normalizedPayload.GetRawText()", logger, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier", metrics, StringComparison.Ordinal);
        Assert.Contains("OutboxTelemetryClassifier", store, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
