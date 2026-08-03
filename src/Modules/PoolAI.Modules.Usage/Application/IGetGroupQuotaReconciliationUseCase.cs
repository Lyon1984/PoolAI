using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Usage.Application;

internal interface IGetGroupQuotaReconciliationUseCase
{
    ValueTask<Result<QuotaReconciliationView>> ExecuteAsync(
        EntityId groupId,
        EntityId? periodId,
        CancellationToken cancellationToken);
}
