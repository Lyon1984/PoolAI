namespace PoolAI.Modules.Identity.Worker;

internal sealed record EmailTransportMessage
{
    internal EmailTransportMessage(
        string fromAddress,
        string fromName,
        string recipient,
        string subject,
        string messageId,
        string body)
    {
        FromAddress = EmailHeaderValueValidator.NormalizeMailbox(
            fromAddress,
            nameof(fromAddress));
        FromName = EmailHeaderValueValidator.ValidateDisplayName(fromName, nameof(fromName));
        Recipient = EmailHeaderValueValidator.NormalizeMailbox(recipient, nameof(recipient));
        Subject = EmailHeaderValueValidator.ValidateSubject(subject, nameof(subject));
        MessageId = EmailHeaderValueValidator.ValidateMessageId(messageId, nameof(messageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        Body = body;
    }

    internal string FromAddress { get; }

    internal string FromName { get; }

    internal string Recipient { get; }

    internal string Subject { get; }

    internal string MessageId { get; }

    internal string Body { get; }
}
