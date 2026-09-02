namespace PoolAI.Modules.Supply.Abstractions;

public interface IRouteCredentialLeaseSource
{
    ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
        RouteCredentialLeaseRequest request,
        CancellationToken cancellationToken);
}
