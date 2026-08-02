namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Checks whether a reservation has an immutable dispatched-attempt fact.
/// </summary>
public interface IAttemptSettlementFactExistenceReader
{
    ValueTask<bool> ExistsForReservationAsync(
        EntityId groupId,
        EntityId periodId,
        EntityId reservationId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
