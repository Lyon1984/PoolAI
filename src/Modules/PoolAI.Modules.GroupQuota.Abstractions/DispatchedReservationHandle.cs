namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record DispatchedReservationHandle(
    ReservationStatus Status,
    ReservationHandle Reservation,
    SettlementProvider Provider,
    string Model,
    TokenEstimateSplit Estimate,
    DateTimeOffset DispatchStartedAt);
