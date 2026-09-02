namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Opaque proof that one specific request process authorized the raw inbound
/// credential and canonical access state and acquired the inbound Group RPM
/// permit. The API Host can carry this value across body validation, but cannot
/// construct it or inspect the canonical snapshots it contains.
/// </summary>
public sealed class GatewayAuthorizedRequest
{
    private readonly GatewayRequestProcess _owner;
    private GatewayCanonicalAccess? _canonical;

    internal GatewayAuthorizedRequest(
        GatewayRequestProcess owner,
        GatewayCanonicalAccess canonical)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _canonical = canonical
            ?? throw new ArgumentNullException(nameof(canonical));
    }

    internal bool TryConsume(
        GatewayRequestProcess owner,
        out GatewayCanonicalAccess canonical)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            canonical = null!;
            return false;
        }

        GatewayCanonicalAccess? available = Interlocked.Exchange(
            ref _canonical,
            null);
        canonical = available!;
        return available is not null;
    }

    public override string ToString() => nameof(GatewayAuthorizedRequest);
}
