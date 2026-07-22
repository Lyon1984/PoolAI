#pragma warning disable MA0048 // The small Group use-case surface is intentionally collocated.
namespace PoolAI.Modules.GroupQuota.Application;

public interface IListGroupsUseCase
{
    ValueTask<Result<GroupPage>> ExecuteAsync(
        ListGroupsQuery query,
        CancellationToken cancellationToken);
}

public interface IGetGroupUseCase
{
    ValueTask<Result<GroupView>> ExecuteAsync(
        GetGroupQuery query,
        CancellationToken cancellationToken);
}

public interface ICreateGroupUseCase
{
    ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
        CreateGroupCommand command,
        CancellationToken cancellationToken);
}

public interface IUpdateGroupUseCase
{
    ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
        UpdateGroupCommand command,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
