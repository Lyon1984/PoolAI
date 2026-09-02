using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

internal interface IGatewaySingleAttemptExecutor
{
    ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
        GatewaySingleAttemptRequest command,
        CancellationToken cancellationToken);
}
