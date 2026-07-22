using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure;

internal sealed class FailClosedGroupSupplyReadiness : IGroupSupplyReadiness
{
    public ValueTask<Result<SupplyReadinessSnapshot>> ObserveAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result.Failure<SupplyReadinessSnapshot>(
            "group_activation_not_ready",
            "Supply readiness is not available yet."));
    }
}
