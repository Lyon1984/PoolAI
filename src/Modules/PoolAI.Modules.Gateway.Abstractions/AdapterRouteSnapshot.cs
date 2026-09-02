namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record AdapterRouteSnapshot(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    UpstreamType Upstream,
    string ClientModel,
    string UpstreamModel,
    Uri UpstreamBaseUri,
    bool SupportsResponses,
    bool SupportsChatCompletions,
    bool SupportsFunctionTools,
    bool SupportsStreaming,
    long SupplyConfigurationVersion,
    long ChannelVersion,
    long AccountVersion,
    long CredentialRevision)
{
    public override string ToString() => nameof(AdapterRouteSnapshot);
}
