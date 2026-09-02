using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewaySingleAttemptExecutor(
    GatewaySingleAttemptProcessManager processManager) :
    IGatewaySingleAttemptExecutor
{
    private readonly GatewaySingleAttemptProcessManager _processManager =
        processManager ?? throw new ArgumentNullException(nameof(processManager));

    public ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
        GatewaySingleAttemptRequest command,
        CancellationToken cancellationToken) =>
        _processManager.ExecuteAsync(command, cancellationToken);
}
