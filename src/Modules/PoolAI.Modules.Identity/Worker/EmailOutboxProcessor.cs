using System.Security.Cryptography;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Worker;

internal sealed class EmailOutboxProcessor
{
    private const string DeadEventName = "email_outbox_dead";
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IEmailOutboxDeliveryStore _deliveryStore;
    private readonly IEmailSecretEnvelope _secretEnvelope;
    private readonly IEmailTransport _transport;
    private readonly IEmailRetryJitter _jitter;
    private readonly IOperationalEventWriter _operationalEventWriter;
    private readonly EmailOutboxWorkerOptions _options;

    internal EmailOutboxProcessor(
        IUnitOfWorkFactory unitOfWorkFactory,
        IEmailOutboxDeliveryStore deliveryStore,
        IEmailSecretEnvelope secretEnvelope,
        IEmailTransport transport,
        IEmailRetryJitter jitter,
        IOperationalEventWriter operationalEventWriter,
        EmailOutboxWorkerOptions options)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _deliveryStore = deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
        _secretEnvelope = secretEnvelope ?? throw new ArgumentNullException(nameof(secretEnvelope));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _operationalEventWriter = operationalEventWriter
            ?? throw new ArgumentNullException(nameof(operationalEventWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async ValueTask<EmailOutboxProcessResult> ProcessNextAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return EmailOutboxProcessResult.OwnershipLost;
        }

        EmailOutboxMessage? message = await ClaimOneAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return EmailOutboxProcessResult.NoWork;
        }

        EmailPreparationResult preparation = Prepare(message);
        if (preparation.Message is null)
        {
            return await DeadAsync(
                jobLock,
                message.Lease,
                preparation.FailureClass
                    ?? throw new InvalidOperationException("Preparation failure requires a class."),
                preparation.FailureReason
                    ?? throw new InvalidOperationException("Preparation failure requires a reason."),
                cancellationToken).ConfigureAwait(false);
        }

        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return EmailOutboxProcessResult.OwnershipLost;
        }

        EmailTransportResult? transportResult = await SendWithHeartbeatAsync(
            jobLock,
            message.Lease,
            preparation.Message,
            cancellationToken).ConfigureAwait(false);
        if (transportResult is null
            || !await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return EmailOutboxProcessResult.OwnershipLost;
        }

        return await HandleTransportResultAsync(
            jobLock,
            message.Lease,
            transportResult,
            cancellationToken).ConfigureAwait(false);
    }

    private EmailPreparationResult Prepare(EmailOutboxMessage message)
    {
        EmailSecretEnvelopePlaintext plaintext;
        try
        {
            plaintext = _secretEnvelope.Decrypt(
                message.RecipientEnvelope,
                message.DeliverySecretEnvelope,
                message.Lease.EmailId);
        }
        catch (Exception exception) when (IsEnvelopeFailure(exception))
        {
            return EmailPreparationResult.Failed(
                EmailDeliveryFailureClass.InvalidEnvelope,
                "invalid_envelope");
        }

        try
        {
            _ = EmailHeaderValueValidator.NormalizeMailbox(
                plaintext.Recipient,
                nameof(plaintext.Recipient));
        }
        catch (ArgumentException)
        {
            return EmailPreparationResult.Failed(
                EmailDeliveryFailureClass.InvalidRecipient,
                "invalid_recipient");
        }

        try
        {
            return EmailPreparationResult.Succeeded(
                PasswordResetEmailTemplate.Render(message, plaintext, _options));
        }
        catch (InvalidOperationException)
        {
            return EmailPreparationResult.Failed(
                EmailDeliveryFailureClass.InvalidTemplate,
                "invalid_template");
        }
        catch (ArgumentException)
        {
            return EmailPreparationResult.Failed(
                EmailDeliveryFailureClass.InvalidMessage,
                "invalid_message");
        }
    }

    private async ValueTask<EmailOutboxProcessResult> HandleTransportResultAsync(
        IWorkerSessionLock jobLock,
        EmailOutboxDeliveryLease lease,
        EmailTransportResult transportResult,
        CancellationToken cancellationToken)
    {
        if (transportResult.Disposition is EmailTransportDisposition.Sent)
        {
            return await MarkSentAsync(lease, cancellationToken).ConfigureAwait(false)
                ? EmailOutboxProcessResult.Processed
                : EmailOutboxProcessResult.OwnershipLost;
        }

        EmailDeliveryFailureClass failureClass = transportResult.FailureClass
            ?? throw new InvalidOperationException("A failed transport result requires a class.");
        if (transportResult.Disposition is EmailTransportDisposition.PermanentFailure)
        {
            return await DeadAsync(
                jobLock,
                lease,
                failureClass,
                FailureCode(failureClass),
                cancellationToken).ConfigureAwait(false);
        }

        DeliveryFailureDecision decision = _options.RetryPolicy.Decide(
            lease.Attempt,
            _jitter.NextFraction());
        if (decision.IsDead)
        {
            return await DeadAsync(
                jobLock,
                lease,
                failureClass,
                "maximum_attempts",
                cancellationToken).ConfigureAwait(false);
        }

        bool released = await ReleaseForRetryAsync(
            lease,
            decision.RetryDelay,
            FailureCode(failureClass),
            cancellationToken).ConfigureAwait(false);
        return released
            ? EmailOutboxProcessResult.Processed
            : EmailOutboxProcessResult.OwnershipLost;
    }

    private async ValueTask<EmailOutboxMessage?> ClaimOneAsync(
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            IReadOnlyList<EmailOutboxMessage> messages = await _deliveryStore.ClaimDueAsync(
                EntityId.New(),
                maximumCount: 1,
                _options.ClaimDuration,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return messages.Count == 0 ? null : messages[0];
        }
    }

    private async ValueTask<EmailTransportResult?> SendWithHeartbeatAsync(
        IWorkerSessionLock jobLock,
        EmailOutboxDeliveryLease lease,
        EmailTransportMessage message,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource sendCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<EmailTransportResult> sendTask = _transport
            .SendAsync(message, sendCancellation.Token)
            .AsTask();
        try
        {
            while (!sendTask.IsCompleted)
            {
                Task heartbeatDelay = Task.Delay(_options.HeartbeatInterval, cancellationToken);
                if (await Task.WhenAny(sendTask, heartbeatDelay).ConfigureAwait(false) == sendTask)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await CancelAndObserveSendAsync(sendTask, sendCancellation).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false)
                    || !await HeartbeatAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    await CancelAndObserveSendAsync(sendTask, sendCancellation).ConfigureAwait(false);
                    return null;
                }
            }
        }
        catch
        {
            await CancelAndObserveSendAsync(sendTask, sendCancellation).ConfigureAwait(false);
            throw;
        }

        return await sendTask.ConfigureAwait(false);
    }

    private static async ValueTask CancelAndObserveSendAsync(
        Task<EmailTransportResult> sendTask,
        CancellationTokenSource sendCancellation)
    {
        try
        {
            await sendCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must not replace ownership loss, caller cancellation, or a fencing exception.
        }

        try
        {
            _ = await sendTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe completion without replacing the authoritative coordination outcome.
        }
    }

    private async ValueTask<bool> HeartbeatAsync(
        EmailOutboxDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            bool updated = await _deliveryStore.HeartbeatAsync(
                lease,
                _options.ClaimDuration,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }
    }

    private async ValueTask<bool> MarkSentAsync(
        EmailOutboxDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            bool updated = await _deliveryStore.MarkSentAsync(
                lease,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }
    }

    private async ValueTask<bool> ReleaseForRetryAsync(
        EmailOutboxDeliveryLease lease,
        TimeSpan retryDelay,
        string failureClass,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            bool updated = await _deliveryStore.ReleaseForRetryAsync(
                lease,
                retryDelay,
                failureClass,
                failureClass,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }
    }

    private async ValueTask<EmailOutboxProcessResult> DeadAsync(
        IWorkerSessionLock jobLock,
        EmailOutboxDeliveryLease lease,
        EmailDeliveryFailureClass failureClass,
        string terminalReason,
        CancellationToken cancellationToken)
    {
        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return EmailOutboxProcessResult.OwnershipLost;
        }

        if (!await MarkDeadAsync(
                lease,
                FailureCode(failureClass),
                terminalReason,
                cancellationToken).ConfigureAwait(false))
        {
            return EmailOutboxProcessResult.OwnershipLost;
        }

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            email_id = lease.EmailId.Value,
            attempt = lease.Attempt,
            generation = lease.Generation,
            failure_class = FailureCode(failureClass),
            terminal_reason = terminalReason,
        });
        await _operationalEventWriter.WriteAsync(
            DeadEventName,
            payload,
            cancellationToken).ConfigureAwait(false);
        return EmailOutboxProcessResult.Processed;
    }

    private async ValueTask<bool> MarkDeadAsync(
        EmailOutboxDeliveryLease lease,
        string failureClass,
        string terminalReason,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            bool updated = await _deliveryStore.MarkDeadAsync(
                lease,
                failureClass,
                terminalReason,
                terminalReason,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (updated)
            {
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }
    }

    private static bool IsEnvelopeFailure(Exception exception) => exception is
        CryptographicException or JsonException or FormatException or InvalidOperationException
        or ArgumentException;

    private static string FailureCode(EmailDeliveryFailureClass failureClass) => failureClass switch
    {
        EmailDeliveryFailureClass.Network => "network",
        EmailDeliveryFailureClass.Dns => "dns",
        EmailDeliveryFailureClass.Tls => "tls",
        EmailDeliveryFailureClass.Timeout => "timeout",
        EmailDeliveryFailureClass.Smtp4xx => "smtp_4xx",
        EmailDeliveryFailureClass.Smtp5xx => "smtp_5xx",
        EmailDeliveryFailureClass.SmtpCapability => "smtp_capability",
        EmailDeliveryFailureClass.SmtpProtocol => "smtp_protocol",
        EmailDeliveryFailureClass.InvalidRecipient => "invalid_recipient",
        EmailDeliveryFailureClass.InvalidMessage => "invalid_message",
        EmailDeliveryFailureClass.InvalidTemplate => "invalid_template",
        EmailDeliveryFailureClass.InvalidEnvelope => "invalid_envelope",
        _ => throw new ArgumentOutOfRangeException(nameof(failureClass)),
    };

    private sealed record EmailPreparationResult(
        EmailTransportMessage? Message,
        EmailDeliveryFailureClass? FailureClass,
        string? FailureReason)
    {
        internal static EmailPreparationResult Succeeded(EmailTransportMessage message) =>
            new(message, null, null);

        internal static EmailPreparationResult Failed(
            EmailDeliveryFailureClass failureClass,
            string failureReason) => new(null, failureClass, failureReason);
    }
}
