namespace PoolAI.Modules.Routing.Worker;

internal sealed record AccountHealthProbeProcessResult(
    DateTimeOffset ObservedAt,
    SupplyHealthCycleStatus CycleStatus,
    SupplyHealthFailureCode FailureCode,
    int ScannedCount,
    int UnknownCount,
    int HealthyCount,
    int DegradedCount,
    int CoolingCount,
    int UnhealthyCount,
    int AuthBlockedCount,
    int ProbeEligibleCount,
    int ProbedCount,
    int HalfOpenProbeCount,
    int SkippedCount,
    int SuccessCount,
    int FailureCount);
