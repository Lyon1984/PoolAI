using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Infrastructure.Health;

internal interface IAccountHealthProbeSnapshotReader
{
    ValueTask<AccountHealthProbeSnapshot?> ReadAsync(
        EntityId accountId,
        CancellationToken cancellationToken);

    ValueTask<bool> IsCurrentAsync(
        AccountHealthProbeSnapshot snapshot,
        CancellationToken cancellationToken);
}
