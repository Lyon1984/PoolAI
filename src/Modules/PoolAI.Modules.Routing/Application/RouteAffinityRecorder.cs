using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.Modules.Routing.Application;

internal sealed class RouteAffinityRecorder(IRouteAffinityStore affinities) : IRouteAffinityRecorder
{
    public ValueTask StoreAsync(EntityId groupId, string sessionHash, EntityId accountId,
        long groupPolicyVersion, long supplyConfigurationVersion, CancellationToken cancellationToken)
    {
        if (groupId.Value.Version != 7 || accountId.Value.Version != 7
            || sessionHash.Length != 32 || sessionHash.Any(static value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            || groupPolicyVersion <= 0 || supplyConfigurationVersion <= 0)
        {
            throw new ArgumentException("The route affinity identity is invalid.", nameof(sessionHash));
        }

        return affinities.SetAsync(groupId, sessionHash,
            new RouteAffinity(accountId, groupPolicyVersion, supplyConfigurationVersion), cancellationToken);
    }
}
