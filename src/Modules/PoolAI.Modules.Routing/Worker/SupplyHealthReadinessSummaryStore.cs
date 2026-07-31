namespace PoolAI.Modules.Routing.Worker;

internal sealed class SupplyHealthReadinessSummaryStore(
    TimeProvider timeProvider) : ISupplyHealthReadinessSummaryStore
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private SupplyHealthReadinessSummary? _current;

    public SupplyHealthReadinessSummary Current =>
        Volatile.Read(ref _current)
        ?? Empty(
            _timeProvider.GetUtcNow(),
            SupplyHealthCycleStatus.Standby,
            SupplyHealthFailureCode.NotOwner);

    public void Update(SupplyHealthReadinessSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Volatile.Write(ref _current, summary);
    }

    internal static SupplyHealthReadinessSummary Empty(
        DateTimeOffset observedAt,
        SupplyHealthCycleStatus status,
        SupplyHealthFailureCode failureCode) =>
        new(
            observedAt,
            status,
            failureCode,
            AccountsSeen: 0,
            UnknownCount: 0,
            HealthyCount: 0,
            DegradedCount: 0,
            CoolingCount: 0,
            UnhealthyCount: 0,
            AuthBlockedCount: 0,
            ProbeEligibleCount: 0,
            AttemptedCount: 0,
            SucceededCount: 0,
            FailedCount: 0);
}
