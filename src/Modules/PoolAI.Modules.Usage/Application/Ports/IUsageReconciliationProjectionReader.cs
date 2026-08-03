using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Usage.Application.Ports;

internal interface IUsageReconciliationProjectionReader
{
    ValueTask<UsageReconciliationProjectionSnapshot> ReadAsync(
        EntityId groupId,
        EntityId periodId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
