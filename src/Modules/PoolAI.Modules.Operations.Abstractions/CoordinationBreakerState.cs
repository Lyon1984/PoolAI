namespace PoolAI.Modules.Operations.Abstractions;

public enum CoordinationBreakerState
{
    Closed,
    Open,
    HalfOpen,
    Unavailable,
}
