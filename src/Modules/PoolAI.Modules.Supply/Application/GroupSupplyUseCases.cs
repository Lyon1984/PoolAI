#pragma warning disable MA0048 // The Group Supply use-case surface is intentionally collocated.
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application;

public interface IGetGroupSupplyConfigurationUseCase
{
    ValueTask<Result<GroupSupplyConfigurationView>> ExecuteAsync(
        GetGroupSupplyConfigurationQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateGroupSupplyConfigurationUseCase
{
    ValueTask<Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
        CreateGroupSupplyConfigurationCommand command,
        CancellationToken cancellationToken);
}

public interface IPatchGroupSupplyConfigurationUseCase
{
    ValueTask<Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>> ExecuteAsync(
        PatchGroupSupplyConfigurationCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
