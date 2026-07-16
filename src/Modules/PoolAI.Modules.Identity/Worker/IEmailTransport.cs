namespace PoolAI.Modules.Identity.Worker;

internal interface IEmailTransport
{
    ValueTask<EmailTransportResult> SendAsync(
        EmailTransportMessage message,
        CancellationToken cancellationToken);
}
