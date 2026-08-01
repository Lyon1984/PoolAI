namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IAttemptSettlementFactReader
{
    ValueTask<Result<AttemptSettlementFact>> GetByAttemptIdAsync(
        EntityId attemptId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
