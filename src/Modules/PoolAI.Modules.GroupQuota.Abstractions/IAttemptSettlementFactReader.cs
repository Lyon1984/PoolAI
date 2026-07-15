namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IAttemptSettlementFactReader
{
    ValueTask<Result<AttemptSettlementFact>> GetByAttemptIdAsync(
        EntityId attemptId,
        CancellationToken cancellationToken);
}
