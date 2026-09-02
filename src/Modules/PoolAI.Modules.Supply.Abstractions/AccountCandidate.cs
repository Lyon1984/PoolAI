namespace PoolAI.Modules.Supply.Abstractions;

public sealed record AccountCandidate(
    EntityId GroupId,
    EntityId ChannelId,
    EntityId AccountId,
    UpstreamProvider Provider,
    string ClientModel,
    string UpstreamModel,
    string UpstreamBaseUrl,
    ChannelCapabilitiesSnapshot Capabilities,
    AccountHealth Health,
    int ConcurrencyLimit,
    int Priority,
    int Weight,
    long ConfigurationVersion,
    long ChannelVersion,
    long AccountVersion,
    long CredentialRevision)
{
    public override string ToString() => nameof(AccountCandidate);
}
