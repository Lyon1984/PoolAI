namespace PoolAI.Modules.Supply.Abstractions;

public interface IModelCatalog
{
    ValueTask<Result<IReadOnlyList<string>>> GetModelsAsync(
        EntityId groupId,
        CancellationToken cancellationToken);
}
