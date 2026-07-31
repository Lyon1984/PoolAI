namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountProbeLeaseCoordinator
{
    ValueTask<Result<IAccountProbeLease>> AcquireAsync(
        AccountProbeLeaseAcquireCommand command,
        CancellationToken cancellationToken);
}
