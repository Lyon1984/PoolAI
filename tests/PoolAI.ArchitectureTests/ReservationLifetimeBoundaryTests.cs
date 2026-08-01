namespace PoolAI.ArchitectureTests;

public sealed class ReservationLifetimeBoundaryTests
{
    [Fact]
    public void ReservationLifetimeUsesCommittedHandleAndShortLedgerCommandsOnly()
    {
        string root = RepositoryRoot.Find();
        string coordinator = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Modules",
            "PoolAI.Modules.Gateway",
            "ReservationLifetimeCoordinator.cs"));

        Assert.Contains(
            "DispatchedReservationHandle reservation",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains("IGroupQuotaLedger", coordinator, StringComparison.Ordinal);
        Assert.Contains("RenewAsync(", coordinator, StringComparison.Ordinal);
        Assert.Contains(
            "ReservationLifetimeCancellation",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains("MaximumDrainDuration", coordinator, StringComparison.Ordinal);

        string[] forbiddenLongLivedDependencies =
        [
            "IUnitOfWork",
            "DbContext",
            "Npgsql",
            "Redis",
            "HttpClient",
        ];
        foreach (string forbidden in forbiddenLongLivedDependencies)
        {
            Assert.DoesNotContain(
                forbidden,
                coordinator,
                StringComparison.Ordinal);
        }
    }
}
