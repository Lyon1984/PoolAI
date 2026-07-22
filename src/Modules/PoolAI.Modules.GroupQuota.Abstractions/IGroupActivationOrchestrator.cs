namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupActivationOrchestrator
{
    ValueTask<Result<GroupActivationResult>> ActivateAsync(
        GroupActivationOrchestrationCommand command,
        CancellationToken cancellationToken);
}
