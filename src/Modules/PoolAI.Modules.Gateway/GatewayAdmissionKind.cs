namespace PoolAI.Modules.Gateway.Application;

public enum GatewayAdmissionKind
{
    NonStream = 0,
    Sse = 1,
    Control = 2,
    Usage = 3,
}
