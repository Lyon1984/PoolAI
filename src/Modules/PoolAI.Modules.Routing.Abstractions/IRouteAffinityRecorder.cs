namespace PoolAI.Modules.Routing.Abstractions;

/// <summary>Records only non-secret, version-fenced same-Group routing affinity.</summary>
public interface IRouteAffinityRecorder
{
    ValueTask StoreAsync(EntityId groupId, string sessionHash, EntityId accountId,
        long groupPolicyVersion, long supplyConfigurationVersion, CancellationToken cancellationToken);
}
