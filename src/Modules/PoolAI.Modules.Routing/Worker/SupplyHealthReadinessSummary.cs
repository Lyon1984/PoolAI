namespace PoolAI.Modules.Routing.Worker;

internal sealed record SupplyHealthReadinessSummary(
    DateTimeOffset ObservedAt,
    SupplyHealthCycleStatus CycleStatus,
    SupplyHealthFailureCode FailureCode,
    int AccountsSeen,
    int UnknownCount,
    int HealthyCount,
    int DegradedCount,
    int CoolingCount,
    int UnhealthyCount,
    int AuthBlockedCount,
    int ProbeEligibleCount,
    int AttemptedCount,
    int SucceededCount,
    int FailedCount);
