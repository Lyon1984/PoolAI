namespace PoolAI.Modules.Routing.Abstractions;

public interface IAccountRouter
{
    ValueTask<Result<IAccountLease>> RouteAsync(
        RouteAccountCommand command,
        CancellationToken cancellationToken);
}
