using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Worker;

internal sealed class OutboxPublisherProcessor
{
    private const string MaximumAttemptsReason = "maximum_attempts";
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IOutboxDeliveryStore _deliveryStore;
    private readonly IntegrationEventDispatcher _dispatcher;
    private readonly IOutboxRetryJitter _jitter;
    private readonly IOperationalEventWriter _operationalEventWriter;
    private readonly OutboxPublisherOptions _options;

    internal OutboxPublisherProcessor(
        IUnitOfWorkFactory unitOfWorkFactory,
        IOutboxDeliveryStore deliveryStore,
        IntegrationEventDispatcher dispatcher,
        IOutboxRetryJitter jitter,
        IOperationalEventWriter operationalEventWriter,
        OutboxPublisherOptions options)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _deliveryStore = deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _operationalEventWriter = operationalEventWriter
            ?? throw new ArgumentNullException(nameof(operationalEventWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    internal async ValueTask<OutboxPublishProcessResult> ProcessNextAsync(
        IWorkerSessionLock jobLock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobLock);
        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return OutboxPublishProcessResult.OwnershipLost;
        }

        OutboxDeliveryMessage? message = await ClaimOneAsync(cancellationToken)
            .ConfigureAwait(false);
        if (message is null)
        {
            return OutboxPublishProcessResult.NoWork;
        }

        if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return OutboxPublishProcessResult.OwnershipLost;
        }

        IntegrationEventConsumeResult? dispatchResult = await DispatchWithHeartbeatAsync(
            jobLock,
            message,
            cancellationToken).ConfigureAwait(false);
        if (dispatchResult is null
            || !await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
        {
            return OutboxPublishProcessResult.OwnershipLost;
        }

        return dispatchResult.Disposition switch
        {
            IntegrationEventConsumeDisposition.Processed
                or IntegrationEventConsumeDisposition.Duplicate =>
                await CompletePublishedAsync(message.Envelope.Lease, cancellationToken)
                    .ConfigureAwait(false),
            IntegrationEventConsumeDisposition.Poison => await CompleteDeadAsync(
                message,
                dispatchResult.Reason
                    ?? throw new InvalidOperationException("A poison result requires a reason."),
                "outbox_poison_dead",
                cancellationToken).ConfigureAwait(false),
            IntegrationEventConsumeDisposition.RetryableFailure => await CompleteRetryAsync(
                message,
                dispatchResult.Reason
                    ?? throw new InvalidOperationException("A retry result requires a reason."),
                cancellationToken).ConfigureAwait(false),
            _ => await CompleteDeadAsync(
                message,
                "invalid_consumer_result",
                "outbox_poison_dead",
                cancellationToken).ConfigureAwait(false),
        };
    }

    private async ValueTask<OutboxDeliveryMessage?> ClaimOneAsync(
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            IReadOnlyList<OutboxDeliveryMessage> messages = await _deliveryStore.ClaimDueAsync(
                new OutboxClaimRequest(
                    EntityId.New(),
                    _dispatcher.Topics,
                    maximumCount: 1,
                    _options.ClaimDuration),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return messages.Count == 0 ? null : messages[0];
        }
    }

    private async ValueTask<IntegrationEventConsumeResult?> DispatchWithHeartbeatAsync(
        IWorkerSessionLock jobLock,
        OutboxDeliveryMessage message,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource dispatchCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<IntegrationEventConsumeResult> dispatchTask = _dispatcher
            .DispatchAsync(message, dispatchCancellation.Token)
            .AsTask();
        try
        {
            while (!dispatchTask.IsCompleted)
            {
                Task heartbeatDelay = Task.Delay(
                    _options.HeartbeatInterval,
                    cancellationToken);
                if (await Task.WhenAny(dispatchTask, heartbeatDelay).ConfigureAwait(false)
                    == dispatchTask)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await CancelAndObserveDispatchAsync(dispatchTask, dispatchCancellation)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!await jobLock.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false)
                    || !await HeartbeatAsync(message.Envelope.Lease, cancellationToken)
                        .ConfigureAwait(false))
                {
                    await CancelAndObserveDispatchAsync(dispatchTask, dispatchCancellation)
                        .ConfigureAwait(false);
                    return null;
                }
            }
        }
        catch
        {
            await CancelAndObserveDispatchAsync(dispatchTask, dispatchCancellation)
                .ConfigureAwait(false);
            throw;
        }

        return await dispatchTask.ConfigureAwait(false);
    }

    private static async ValueTask CancelAndObserveDispatchAsync(
        Task<IntegrationEventConsumeResult> dispatchTask,
        CancellationTokenSource dispatchCancellation)
    {
        try
        {
            await dispatchCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup must not replace the authoritative ownership or cancellation result.
        }

        try
        {
            _ = await dispatchTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Observe completion without replacing the authoritative coordination result.
        }
    }

    private async ValueTask<bool> HeartbeatAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken) =>
        await ExecuteLeaseMutationAsync(
            (context, token) => _deliveryStore.HeartbeatAsync(
                lease,
                _options.ClaimDuration,
                context,
                token),
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<OutboxPublishProcessResult> CompletePublishedAsync(
        OutboxDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        bool published = await ExecuteLeaseMutationAsync(
            (context, token) => _deliveryStore.MarkPublishedAsync(
                lease,
                context,
                token),
            cancellationToken).ConfigureAwait(false);
        return published
            ? OutboxPublishProcessResult.Processed
            : OutboxPublishProcessResult.OwnershipLost;
    }

    private async ValueTask<OutboxPublishProcessResult> CompleteRetryAsync(
        OutboxDeliveryMessage message,
        string reason,
        CancellationToken cancellationToken)
    {
        DeliveryFailureDecision decision = _options.RetryPolicy.Decide(
            message.Envelope.Lease.Attempt,
            _jitter.NextFraction());
        if (decision.IsDead)
        {
            return await CompleteDeadAsync(
                message,
                MaximumAttemptsReason,
                "outbox_max_attempts_dead",
                cancellationToken).ConfigureAwait(false);
        }

        bool released = await ExecuteLeaseMutationAsync(
            (context, token) => _deliveryStore.ReleaseForRetryAsync(
                message.Envelope.Lease,
                decision.RetryDelay,
                reason,
                context,
                token),
            cancellationToken).ConfigureAwait(false);
        return released
            ? OutboxPublishProcessResult.Processed
            : OutboxPublishProcessResult.OwnershipLost;
    }

    private async ValueTask<OutboxPublishProcessResult> CompleteDeadAsync(
        OutboxDeliveryMessage message,
        string reason,
        string eventName,
        CancellationToken cancellationToken)
    {
        bool dead = await ExecuteLeaseMutationAsync(
            (context, token) => _deliveryStore.MarkDeadAsync(
                message.Envelope.Lease,
                reason,
                context,
                token),
            cancellationToken).ConfigureAwait(false);
        if (!dead)
        {
            return OutboxPublishProcessResult.OwnershipLost;
        }

        Dictionary<string, object?> diagnostics = new(StringComparer.Ordinal)
        {
            ["topic"] = OutboxTelemetryClassifier.NormalizeTopic(message.Envelope.Topic),
            ["event_type"] = OutboxTelemetryClassifier.NormalizeEventType(
                message.Envelope.EventType),
            ["schema_version"] = message.Envelope.SchemaVersion,
            ["reason"] = OutboxTelemetryClassifier.NormalizeReason(reason),
            ["attempt"] = message.Envelope.Lease.Attempt,
        };
        if (string.Equals(eventName, "outbox_poison_dead", StringComparison.Ordinal))
        {
            diagnostics["severity"] = "P0";
        }

        JsonElement payload = JsonSerializer.SerializeToElement(diagnostics);
        await _operationalEventWriter.WriteAsync(
            eventName,
            payload,
            CancellationToken.None).ConfigureAwait(false);
        return OutboxPublishProcessResult.Processed;
    }

    private async ValueTask<bool> ExecuteLeaseMutationAsync(
        Func<IUnitOfWorkContext, CancellationToken, ValueTask<bool>> mutation,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken).ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            bool updated = await mutation(unitOfWork.Context, cancellationToken)
                .ConfigureAwait(false);
            if (updated)
            {
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return updated;
        }
    }
}
