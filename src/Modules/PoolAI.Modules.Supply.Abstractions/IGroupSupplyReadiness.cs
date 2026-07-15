namespace PoolAI.Modules.Supply.Abstractions;

public interface IGroupSupplyReadiness
{
    ValueTask<Result<SupplyReadinessSnapshot>> ObserveAsync(
        EntityId groupId,
        CancellationToken cancellationToken);
}
