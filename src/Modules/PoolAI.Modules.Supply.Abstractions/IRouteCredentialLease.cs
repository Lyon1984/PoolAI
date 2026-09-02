namespace PoolAI.Modules.Supply.Abstractions;

public interface IRouteCredentialLease : IDisposable
{
    void TransferOnce(RouteCredentialReader reader);
}
