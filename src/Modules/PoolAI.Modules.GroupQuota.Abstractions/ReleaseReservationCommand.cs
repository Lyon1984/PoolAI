namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record ReleaseReservationCommand(
    ReservationHandle Reservation,
    string Reason);
