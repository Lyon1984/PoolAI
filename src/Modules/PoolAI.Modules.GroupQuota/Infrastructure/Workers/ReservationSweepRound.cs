using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Workers;

internal delegate ValueTask ReservationSweepRound(
    IWorkerSessionLock jobLock,
    int pageSize,
    CancellationToken cancellationToken);
