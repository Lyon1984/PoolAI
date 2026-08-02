namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Reads one immutable completion-hour snapshot without exposing GroupQuota persistence.
/// </summary>
public interface IAttemptSettlementHourFactReader
{
    ValueTask<AttemptSettlementHourSnapshot?> ReadForAttemptAsync(
        EntityId groupId,
        EntityId periodId,
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
