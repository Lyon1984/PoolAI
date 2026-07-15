namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public interface ISubscriptionAccessReader
{
    ValueTask<Result<SubscriptionAccessSnapshot>> GetEffectiveAccessAsync(
        EntityId userId,
        EntityId groupId,
        CancellationToken cancellationToken);
}
