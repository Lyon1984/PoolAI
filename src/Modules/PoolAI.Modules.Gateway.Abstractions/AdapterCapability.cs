namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record AdapterCapability(
    InboundProtocol Protocol,
    UpstreamType Upstream,
    AdapterOperation Operation,
    bool CanProveNoRequestBytesWritten,
    bool SupportsVerifiedIdempotentReplay,
    AdapterRejectedStatusEvidence ConfirmedNoExecutionStatuses =
        AdapterRejectedStatusEvidence.None)
{
    public bool ConfirmsNoExecutionForStatus(int statusCode)
    {
        AdapterRejectedStatusEvidence required = statusCode switch
        {
            401 => AdapterRejectedStatusEvidence.Unauthorized,
            403 => AdapterRejectedStatusEvidence.Forbidden,
            429 => AdapterRejectedStatusEvidence.TooManyRequests,
            _ => AdapterRejectedStatusEvidence.None,
        };
        return required != AdapterRejectedStatusEvidence.None
            && (ConfirmedNoExecutionStatuses & required) == required;
    }
}
