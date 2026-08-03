using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Usage.Worker;

internal delegate ValueTask<QuotaReconciliationProcessResult>
    QuotaReconciliationScanRound(
        IWorkerSessionLock jobLock,
        int pageSize,
        CancellationToken cancellationToken);
