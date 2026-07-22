namespace PoolAI.Modules.GroupQuota.Abstractions;

public interface IGroupActivationIdempotencyPreflight
{
    ValueTask<Result<GroupActivationResult?>> TryReplayAsync(
        GroupActivationOrchestrationCommand command,
        CancellationToken cancellationToken);
}
