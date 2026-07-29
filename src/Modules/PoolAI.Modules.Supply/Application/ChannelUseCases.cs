#pragma warning disable MA0048 // The small Channel use-case surface is intentionally collocated.
using PoolAI.BuildingBlocks;

namespace PoolAI.Modules.Supply.Application;

public interface IListChannelsUseCase
{
    ValueTask<Result<ChannelPage>> ExecuteAsync(
        ListChannelsQuery query,
        CancellationToken cancellationToken);
}

public interface IGetChannelUseCase
{
    ValueTask<Result<ChannelView>> ExecuteAsync(
        GetChannelQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateChannelUseCase
{
    ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
        CreateChannelCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateChannelUseCase
{
    ValueTask<Result<SupplyCommandOutcome<ChannelView>>> ExecuteAsync(
        UpdateChannelCommand command,
        CancellationToken cancellationToken);
}

public interface IRetireChannelUseCase
{
    ValueTask<Result<SupplyCommandOutcome>> ExecuteAsync(
        RetireChannelCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
