namespace PoolAI.Modules.Operations.Abstractions;

public interface ICoordinationLeaseSet
{
    ValueTask<CoordinationLeaseAcquireResult> AcquireAsync(
        CoordinationLeaseAcquireRequest request,
        CancellationToken cancellationToken);

    ValueTask<CoordinationLeaseRenewResult> RenewAsync(
        CoordinationLeaseOwner request,
        CancellationToken cancellationToken);

    ValueTask<CoordinationLeaseReleaseResult> ReleaseAsync(
        CoordinationLeaseOwner request,
        CancellationToken cancellationToken);
}
