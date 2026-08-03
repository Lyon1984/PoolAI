using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.Modules.Usage.Infrastructure.Workers;

internal delegate ValueTask<BoundedUsagePeriodRebuildResult>
    UsageProjectionRebuildRun(
        IWorkerSessionLock jobLock,
        BoundedUsagePeriodRebuildRequest request,
        CancellationToken cancellationToken);
