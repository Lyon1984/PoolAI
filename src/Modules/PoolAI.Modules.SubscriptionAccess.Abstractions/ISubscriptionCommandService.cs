namespace PoolAI.Modules.SubscriptionAccess.Abstractions;

public interface ISubscriptionCommandService
{
    ValueTask<Result<SubscriptionAccessSnapshot>> ChangeAsync(
        ChangeSubscriptionCommand command,
        CancellationToken cancellationToken);
}
