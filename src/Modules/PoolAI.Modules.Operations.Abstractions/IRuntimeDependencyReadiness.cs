namespace PoolAI.Modules.Operations.Abstractions;

public interface IRuntimeDependencyReadiness
{
    ValueTask<RuntimeDependencyReadiness> CheckAsync(CancellationToken cancellationToken);
}
