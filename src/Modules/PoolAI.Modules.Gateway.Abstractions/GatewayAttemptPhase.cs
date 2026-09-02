namespace PoolAI.Modules.Gateway.Abstractions;

public enum GatewayAttemptPhase
{
    Prepared,
    DispatchedNoDownstreamHeaders,
    DownstreamHeadersCommitted,
    BusinessOutputStarted,
}
