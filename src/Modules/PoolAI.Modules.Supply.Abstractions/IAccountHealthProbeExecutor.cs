namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountHealthProbeExecutor
{
    ValueTask<Result<AccountHealthProbeResult>> ProbeAsync(
        EntityId accountId,
        CancellationToken cancellationToken);
}
