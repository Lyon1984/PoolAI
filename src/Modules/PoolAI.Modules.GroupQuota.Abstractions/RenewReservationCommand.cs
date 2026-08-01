namespace PoolAI.Modules.GroupQuota.Abstractions;

public sealed record RenewReservationCommand(
    ReservationHandle Reservation,
    long RenewalSequence);
