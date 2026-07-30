using System.Text.Json.Serialization;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Application;

namespace PoolAI.Modules.Routing.Infrastructure;

internal sealed class CoordinationRouteAffinityStore(
    ICoordinationValueStore values) : IRouteAffinityStore
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(60);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly ICoordinationValueStore _values =
        values ?? throw new ArgumentNullException(nameof(values));

    public async ValueTask<RouteAffinity?> GetAsync(
        EntityId groupId,
        string sessionHash,
        CancellationToken cancellationToken)
    {
        CoordinationValueReadResult result = await _values
            .GetAndRefreshAsync(
                Key(groupId, sessionHash),
                TimeToLive,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Disposition != CoordinationValueReadDisposition.Found
            || result.Value is null)
        {
            return null;
        }

        try
        {
            AffinityPayload? payload = JsonSerializer.Deserialize<AffinityPayload>(
                result.Value,
                JsonOptions);
            return payload is not null
                && payload.GroupPolicyVersion > 0
                && payload.SupplyConfigurationVersion > 0
                && Guid.TryParseExact(payload.AccountId, "D", out Guid accountId)
                && accountId != Guid.Empty
                && string.Equals(
                    accountId.ToString("D"),
                    payload.AccountId,
                    StringComparison.Ordinal)
                    ? new RouteAffinity(
                        new EntityId(accountId),
                        payload.GroupPolicyVersion,
                        payload.SupplyConfigurationVersion)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask SetAsync(
        EntityId groupId,
        string sessionHash,
        RouteAffinity affinity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(affinity);
        string payload = JsonSerializer.Serialize(
            new AffinityPayload(
                affinity.AccountId.Value.ToString("D"),
                affinity.GroupPolicyVersion,
                affinity.SupplyConfigurationVersion),
            JsonOptions);
        _ = await _values.SetAsync(
            Key(groupId, sessionHash),
            payload,
            TimeToLive,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string Key(EntityId groupId, string sessionHash) =>
        $"sticky:v1:{{{groupId.Value:D}}}:{{{sessionHash}}}";

    private sealed record AffinityPayload(
        [property: JsonPropertyName("account_id")] string AccountId,
        [property: JsonPropertyName("group_policy_version")] long GroupPolicyVersion,
        [property: JsonPropertyName("supply_configuration_version")] long SupplyConfigurationVersion);
}
