using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed record GatewayAttemptEvidence(
    GatewayRequestWriteEvidence RequestWriteEvidence,
    NormalizedUpstreamResult? UpstreamResult,
    string? TransportErrorCode,
    bool ConfirmedNoExecution)
{
    internal NormalizedUpstreamUsage? Usage => UpstreamResult?.Usage;
}
