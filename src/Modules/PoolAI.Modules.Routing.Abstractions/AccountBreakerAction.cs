namespace PoolAI.Modules.Routing.Abstractions;

public enum AccountBreakerAction
{
    None,
    MarkHealthy,
    MarkDegraded,
    MarkCooling,
    MarkUnhealthy,
    MarkUnknown,
}
