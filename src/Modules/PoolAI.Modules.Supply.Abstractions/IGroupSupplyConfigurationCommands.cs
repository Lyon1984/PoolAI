namespace PoolAI.Modules.Supply.Abstractions;

public interface IGroupSupplyConfigurationCommands
{
    ValueTask<Result<long>> ReplaceAsync(
        ReplaceGroupSupplyConfigurationCommand command,
        CancellationToken cancellationToken);
}
