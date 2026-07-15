namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupActivationCommand
{
    ValueTask<Result<GroupActivationResult>> ActivateAsync(
        ActivateGroupCommand command,
        CancellationToken cancellationToken);
}
