namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record MarkReservationDispatchedCommand(
    ReservationHandle Reservation,
    SettlementProvider Provider,
    string Model,
    TokenEstimateSplit Estimate);
