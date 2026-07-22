namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public interface IUserSubscriptionGrantReader
{
    ValueTask<Result<IReadOnlyList<UserSubscriptionGrantSnapshot>>> ListActiveAsync(
        EntityId userId,
        CancellationToken cancellationToken);
}
