namespace PoolAI.Modules.Operations.Abstractions;

public interface ICoordinationLeaseCounter
{
    ValueTask<CoordinationLeaseCountResult> CountActiveAsync(
        IReadOnlyList<string> keyBases,
        CancellationToken cancellationToken);
}
