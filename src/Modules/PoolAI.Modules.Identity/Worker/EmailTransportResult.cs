namespace PoolAI.Modules.Identity.Worker;

internal sealed record EmailTransportResult
{
    private EmailTransportResult(
        EmailTransportDisposition disposition,
        EmailDeliveryFailureClass? failureClass)
    {
        if (disposition is EmailTransportDisposition.Sent && failureClass is not null
            || disposition is not EmailTransportDisposition.Sent && failureClass is null)
        {
            throw new ArgumentException(
                "Email transport result is inconsistent.",
                nameof(failureClass));
        }

        Disposition = disposition;
        FailureClass = failureClass;
    }

    internal EmailTransportDisposition Disposition { get; }

    internal EmailDeliveryFailureClass? FailureClass { get; }

    internal static EmailTransportResult Sent { get; } = new(
        EmailTransportDisposition.Sent,
        null);

    internal static EmailTransportResult Transient(EmailDeliveryFailureClass failureClass) =>
        new(EmailTransportDisposition.TransientFailure, failureClass);

    internal static EmailTransportResult Permanent(EmailDeliveryFailureClass failureClass) =>
        new(EmailTransportDisposition.PermanentFailure, failureClass);
}
