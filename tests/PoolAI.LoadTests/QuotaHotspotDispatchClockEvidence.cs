namespace PoolAI.LoadTests;

public sealed record QuotaHotspotDispatchClockEvidence(
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DispatchStartedAt,
    DateTimeOffset EventDispatchStartedAt,
    int DispatchEventCount,
    int DispatchOutboxCount);
