namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record AdapterCapability(
    InboundProtocol Protocol,
    UpstreamType Upstream,
    AdapterOperation Operation,
    bool CanProveNoRequestBytesWritten,
    bool SupportsVerifiedIdempotentReplay);
