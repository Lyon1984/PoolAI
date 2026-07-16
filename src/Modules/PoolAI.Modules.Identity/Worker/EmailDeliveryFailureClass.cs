namespace PoolAI.Modules.Identity.Worker;

internal enum EmailDeliveryFailureClass
{
    Network,
    Dns,
    Tls,
    Timeout,
    Smtp4xx,
    Smtp5xx,
    SmtpCapability,
    SmtpProtocol,
    InvalidRecipient,
    InvalidMessage,
    InvalidTemplate,
    InvalidEnvelope,
}
