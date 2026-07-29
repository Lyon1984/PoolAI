namespace PoolAI.Modules.Supply.Abstractions;

public interface IGroupSupplyConfigurationReader
{
    ValueTask<Result<GroupSupplyConfigurationSnapshot>> GetCurrentAsync(
        EntityId groupId,
        CancellationToken cancellationToken);
}
