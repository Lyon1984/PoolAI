namespace PoolAI.Modules.Supply.Abstractions;

public sealed record RouteCredentialLeaseRequest(
    EntityId AccountId,
    long AccountVersion,
    long CredentialRevision,
    UpstreamProvider Provider,
    Uri UpstreamBaseUri)
{
    public override string ToString() => nameof(RouteCredentialLeaseRequest);
}
