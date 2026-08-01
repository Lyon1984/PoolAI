namespace PoolAI.Modules.Gateway.Application;

public interface IReservationLifetimeOperation
{
    ValueTask<ReservationSettlementEvidence> ExecuteAsync(
        ReservationLifetimeCancellation cancellation);
}
