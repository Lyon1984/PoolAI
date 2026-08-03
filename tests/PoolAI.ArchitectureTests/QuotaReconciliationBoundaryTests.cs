namespace PoolAI.ArchitectureTests;

public sealed class QuotaReconciliationBoundaryTests
{
    [Fact]
    public void ContinuousScannerIsWorkerOnlyAndSessionLocked()
    {
        string root = RepositoryRoot.Find();
        string api = Read(root, "src", "PoolAI.Api", "Program.cs");
        string worker = Read(root, "src", "PoolAI.Worker", "Program.cs");
        string baseRegistration = Usage(root, "DependencyInjection.cs");
        string workerRegistration = Usage(
            root,
            "QuotaReconciliationWorkerDependencyInjection.cs");
        string hostedService = Usage(
            root,
            "Infrastructure",
            "Workers",
            "QuotaReconciliationService.cs");

        Assert.DoesNotContain("AddUsageQuotaReconciliationWorker", api, StringComparison.Ordinal);
        Assert.Equal(1, Count(worker, ".AddUsageQuotaReconciliationWorker()"));
        Assert.DoesNotContain("AddUsageProjectionRebuildWorker", api, StringComparison.Ordinal);
        Assert.Equal(1, Count(worker, ".AddUsageProjectionRebuildWorker(builder.Configuration)"));
        Assert.Contains(
            "WorkerJobs:UsageRebuild:Enabled",
            worker,
            StringComparison.Ordinal);
        int oneShotMode = worker.IndexOf(
            "WorkerJobs:UsageRebuild:Enabled",
            StringComparison.Ordinal);
        int oneShotRegistration = worker.IndexOf(
            ".AddUsageProjectionRebuildWorker(builder.Configuration)",
            StringComparison.Ordinal);
        int normalMode = worker.IndexOf("else", oneShotRegistration, StringComparison.Ordinal);
        int continuousRegistration = worker.IndexOf(
            ".AddUsageQuotaReconciliationWorker()",
            StringComparison.Ordinal);
        Assert.True(oneShotMode < oneShotRegistration);
        Assert.True(oneShotRegistration < normalMode);
        Assert.True(normalMode < continuousRegistration);
        string[] normalWorkerRegistrations =
        [
            ".AddRoutingHealthModule(builder.Configuration)",
            ".AddIdentityEmailOutboxWorker(builder.Configuration)",
            ".AddGroupQuotaReservationSweeper(builder.Configuration)",
            ".AddSupplyCredentialRewrapWorker(builder.Configuration)",
            ".AddOperationsOutboxPublisher(builder.Configuration)",
            ".AddUsageQuotaReconciliationWorker()",
        ];
        Assert.All(
            normalWorkerRegistrations,
            registration => Assert.True(
                normalMode < worker.IndexOf(registration, StringComparison.Ordinal),
                $"Normal Worker registration escaped one-shot isolation: {registration}"));
        Assert.DoesNotContain("IHostedService", baseRegistration, StringComparison.Ordinal);
        Assert.Contains("IHostedService", workerRegistration, StringComparison.Ordinal);
        Assert.Contains("WorkerJobs.QuotaReconciliation", hostedService, StringComparison.Ordinal);
        Assert.Contains("IWorkerSessionLockProvider", hostedService, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(30)", hostedService, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(25)", hostedService, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiComposesOnlyUsageAndGroupQuotaReadPorts()
    {
        string root = RepositoryRoot.Find();
        string endpoint = Usage(
            root,
            "Endpoints",
            "QuotaReconciliationEndpointMappings.cs");
        string useCase = Usage(
            root,
            "Application",
            "QuotaReconciliationService.cs");

        Assert.Contains("IGetGroupQuotaReconciliationUseCase", endpoint, StringComparison.Ordinal);
        Assert.Contains("IGroupQuotaReconciliationFactReader", useCase, StringComparison.Ordinal);
        Assert.Contains("IUsageReconciliationProjectionReader", useCase, StringComparison.Ordinal);
        Assert.DoesNotContain("IQuotaDeliveryHealthReader", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("IQuotaDeliveryHealthReader", useCase, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("IUnitOfWork", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void ScannerComposesPortsWithoutRepairAuthority()
    {
        string root = RepositoryRoot.Find();
        string processor = Usage(
            root,
            "Worker",
            "QuotaReconciliationProcessor.cs");

        Assert.Contains("IGroupQuotaReconciliationFactReader", processor, StringComparison.Ordinal);
        Assert.Contains("IUsageReconciliationProjectionReader", processor, StringComparison.Ordinal);
        Assert.Contains("IQuotaDeliveryHealthReader", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("DbContext", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceAsync", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("Replay", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("adminUpdateGroup", processor, StringComparison.Ordinal);
        Assert.DoesNotContain("StackExchange.Redis", processor, StringComparison.Ordinal);
    }

    [Fact]
    public void MetricsUseOnlyTheFrozenLowCardinalityLabels()
    {
        string root = RepositoryRoot.Find();
        string metrics = Usage(
            root,
            "Infrastructure",
            "Observability",
            "QuotaReconciliationMetrics.cs");
        string[] requiredNames =
        [
            "poolai_quota_reserved_tokens",
            "poolai_usage_aggregation_lag_seconds",
            "poolai_quota_reconciliation_delta_tokens",
            "poolai_quota_reconciliation_mismatched_groups",
            "poolai_quota_reservation_leak_candidates",
            "poolai_quota_reservation_oldest_overdue_seconds",
            "poolai_quota_overage_tokens",
        ];

        Assert.All(
            requiredNames,
            name => Assert.Contains(name, metrics, StringComparison.Ordinal));
        Assert.Contains("\"group_tier\", DefaultGroupTier", metrics, StringComparison.Ordinal);
        Assert.Contains("\"kind\", kind", metrics, StringComparison.Ordinal);
        Assert.Contains("\"worker\", WorkerName", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("group_id", metrics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("period_id", metrics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attempt_id", metrics, StringComparison.OrdinalIgnoreCase);
    }

    private static string Usage(string root, params string[] path) =>
        File.ReadAllText(Path.Combine(
            [root, "src", "Modules", "PoolAI.Modules.Usage", .. path]));

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

    private static int Count(string source, string value)
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
