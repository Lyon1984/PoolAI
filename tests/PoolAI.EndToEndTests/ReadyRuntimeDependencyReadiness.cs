using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.EndToEndTests;

internal sealed class ReadyRuntimeDependencyReadiness : IRuntimeDependencyReadiness
{
    public RuntimeDependencyReadiness Result { get; set; } = new(true, null);

    public ValueTask<RuntimeDependencyReadiness> CheckAsync(
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Result);
}
