using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed record GatewayUpstreamTransportResult(
    Result<NormalizedUpstreamResult> Response,
    GatewayRequestWriteEvidence WriteEvidence,
    bool ConfirmedNoExecution)
{
    public bool RequestBytesWritten =>
        WriteEvidence != GatewayRequestWriteEvidence.ConfirmedNotWritten;

    public override string ToString() => nameof(GatewayUpstreamTransportResult);
}
