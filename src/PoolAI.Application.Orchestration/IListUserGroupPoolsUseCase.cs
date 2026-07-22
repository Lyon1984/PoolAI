using PoolAI.BuildingBlocks;

namespace PoolAI.Application.Orchestration;

public interface IListUserGroupPoolsUseCase
{
    ValueTask<Result<IReadOnlyList<UserGroupPoolView>>> ExecuteAsync(
        ListUserGroupPoolsQuery query,
        CancellationToken cancellationToken);
}
