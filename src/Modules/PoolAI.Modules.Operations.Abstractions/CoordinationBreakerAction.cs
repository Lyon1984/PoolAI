namespace PoolAI.Modules.Operations.Abstractions;

public enum CoordinationBreakerAction
{
    None,
    WriteHealthy,
    WriteDegraded,
    WriteCooling,
    WriteUnhealthy,
    WriteUnknown,
}
