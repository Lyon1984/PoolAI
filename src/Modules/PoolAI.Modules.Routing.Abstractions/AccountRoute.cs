namespace PoolAI.Modules.Routing.Abstractions;

public sealed record AccountRoute(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    AccountRouteProvider Provider,
    string ClientModel,
    string UpstreamModel,
    Uri UpstreamBaseUri,
    AccountRouteCapabilities Capabilities,
    DateTimeOffset LeaseExpiresAt,
    long SupplyConfigurationVersion,
    long ChannelVersion,
    long AccountVersion,
    long CredentialRevision)
{
    public override string ToString() => nameof(AccountRoute);
}
