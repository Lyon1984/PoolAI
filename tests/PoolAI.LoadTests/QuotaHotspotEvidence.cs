namespace PoolAI.LoadTests;

public sealed record QuotaHotspotEvidence(
    string TotalTokens,
    string ConsumedTokens,
    string ReservedTokens,
    string RemainingTokens,
    int RequestCount,
    int ReservationCount,
    int SettledReservationCount,
    int ReleasedReservationCount,
    int AttemptCount,
    int ReservedEventCount,
    int DispatchEventCount,
    int SettledEventCount,
    int ReleasedEventCount,
    int QuotaEventCount,
    int OutboxCount,
    int AuditCount,
    int DuplicateIdentityCount,
    int InvariantViolationCount,
    int NarrowNumericColumnCount);
