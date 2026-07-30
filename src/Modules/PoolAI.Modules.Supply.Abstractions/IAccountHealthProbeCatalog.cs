namespace PoolAI.Modules.Supply.Abstractions;

public interface IAccountHealthProbeCatalog
{
    ValueTask<Result<IReadOnlyList<AccountHealthProbeCandidate>>> GetDueBatchAsync(
        EntityId? afterExclusive,
        int maximumCount,
        TimeSpan healthyProbeInterval,
        CancellationToken cancellationToken);
}
