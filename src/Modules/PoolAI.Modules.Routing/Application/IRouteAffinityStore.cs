namespace PoolAI.Modules.Routing.Application;

internal interface IRouteAffinityStore
{
    ValueTask<RouteAffinity?> GetAsync(
        EntityId groupId,
        string sessionHash,
        CancellationToken cancellationToken);

    ValueTask SetAsync(
        EntityId groupId,
        string sessionHash,
        RouteAffinity affinity,
        CancellationToken cancellationToken);
}
