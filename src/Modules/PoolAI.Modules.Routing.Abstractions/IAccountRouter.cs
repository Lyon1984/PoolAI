namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountRouter
{
    ValueTask<Result<AccountRoute>> RouteAsync(
        RouteAccountCommand command,
        CancellationToken cancellationToken);
}
