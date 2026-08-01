namespace PoolAI.ArchitectureTests;

public sealed class ReservationSweeperBoundaryTests
{
    [Fact]
    public void ReservationSweeperIsWorkerOnlyAndUsesPostgresSessionLock()
    {
        string root = RepositoryRoot.Find();
        string apiProgram = Read(root, "src", "PoolAI.Api", "Program.cs");
        string workerProgram = Read(root, "src", "PoolAI.Worker", "Program.cs");
        string baseRegistration = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota",
            "DependencyInjection.cs");
        string workerRegistration = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota",
            "ReservationWorkerDependencyInjection.cs");
        string hostedService = Read(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.GroupQuota",
            "Infrastructure",
            "Workers",
            "ReservationSweeperService.cs");

        AssertBaseRegistrationHasNoWorkerRuntime(apiProgram, baseRegistration);
        AssertWorkerRegistration(workerProgram, workerRegistration);
        AssertHostedServiceContract(hostedService, workerRegistration);
    }

    private static void AssertBaseRegistrationHasNoWorkerRuntime(
        string apiProgram,
        string baseRegistration)
    {
        Assert.DoesNotContain(
            ".AddGroupQuotaReservationSweeper(",
            apiProgram,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReservationSweeperProcessor",
            baseRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReservationSweeperService",
            baseRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IHostedService",
            baseRegistration,
            StringComparison.Ordinal);
    }

    private static void AssertWorkerRegistration(
        string workerProgram,
        string workerRegistration)
    {
        Assert.Equal(
            1,
            CountOccurrences(
                workerProgram,
                ".AddGroupQuotaReservationSweeper(builder.Configuration)"));
        Assert.Contains(
            "TryAddSingleton<ReservationSweeperProcessor>()",
            workerRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ServiceDescriptor.Singleton<IHostedService,",
            workerRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReservationSweeperService>()",
            workerRegistration,
            StringComparison.Ordinal);
    }

    private static void AssertHostedServiceContract(
        string hostedService,
        string workerRegistration)
    {
        Assert.Contains(
            "TimeSpan.FromSeconds(30)",
            hostedService,
            StringComparison.Ordinal);
        Assert.Contains(
            "TimeSpan.FromSeconds(25)",
            hostedService,
            StringComparison.Ordinal);
        Assert.Contains(
            "PeriodicTimer",
            hostedService,
            StringComparison.Ordinal);
        Assert.Contains(
            "IWorkerSessionLockProvider",
            hostedService,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkerJobs.ReservationSweeper",
            hostedService,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Redis", hostedService, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Redis", workerRegistration, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string root, params string[] path) =>
        File.ReadAllText(Path.Combine([root, .. path]));

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
}
