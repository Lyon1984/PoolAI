using System.Diagnostics;
using System.Diagnostics.Metrics;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Infrastructure.Email;

internal sealed class EmailOutboxMetrics : IDisposable
{
    internal const string MeterName = "PoolAI.Identity.EmailOutbox";
    private static readonly long RefreshTicks = (long)(Stopwatch.Frequency * 5d);
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IEmailOutboxDeliveryStore _deliveryStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Meter _meter = new(MeterName);
    private EmailOutboxObservabilitySnapshot _snapshot = EmailOutboxObservabilitySnapshot.Empty;
    private long _nextRefreshTimestamp;

    internal EmailOutboxMetrics(
        IUnitOfWorkFactory unitOfWorkFactory,
        IEmailOutboxDeliveryStore deliveryStore)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _deliveryStore = deliveryStore ?? throw new ArgumentNullException(nameof(deliveryStore));
        _meter.CreateObservableGauge(
            "poolai_email_outbox_pending",
            () => Volatile.Read(ref _snapshot).PendingCount,
            unit: "{message}",
            description: "Durable email messages that have not reached a terminal state.");
        _meter.CreateObservableGauge(
            "poolai_email_outbox_oldest_age_seconds",
            () => Volatile.Read(ref _snapshot).OldestAgeSeconds,
            unit: "s",
            description: "Age of the oldest durable email message that is not terminal.");
        _meter.CreateObservableGauge(
            "poolai_email_outbox_dead",
            () => Volatile.Read(ref _snapshot).DeadCount,
            unit: "{message}",
            description: "Durable email messages in the dead state.");
        _meter.CreateObservableCounter(
            "poolai_email_outbox_failures_total",
            ObserveFailures,
            unit: "{failure}",
            description: "Durable email delivery failures by bounded classification.");
    }

    internal async ValueTask RefreshIfDueAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && now < Volatile.Read(ref _nextRefreshTimestamp))
        {
            return;
        }

        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            now = Stopwatch.GetTimestamp();
            if (!force && now < Volatile.Read(ref _nextRefreshTimestamp))
            {
                return;
            }

            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken).ConfigureAwait(false);
            await using (unitOfWork.ConfigureAwait(false))
            {
                EmailOutboxObservabilitySnapshot snapshot =
                    await _deliveryStore.ReadObservabilityAsync(
                        unitOfWork.Context,
                        cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _snapshot, snapshot);
            }

            Volatile.Write(ref _nextRefreshTimestamp, now + RefreshTicks);
        }
        finally
        {
            _ = _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        _meter.Dispose();
        _refreshGate.Dispose();
    }

    private IEnumerable<Measurement<long>> ObserveFailures()
    {
        EmailOutboxObservabilitySnapshot snapshot = Volatile.Read(ref _snapshot);
        foreach (EmailOutboxFailureMetric failure in snapshot.Failures)
        {
            yield return new Measurement<long>(
                failure.Count,
                new KeyValuePair<string, object?>("failure_class", failure.FailureClass),
                new KeyValuePair<string, object?>("outcome", failure.Outcome),
                new KeyValuePair<string, object?>("terminal_reason", failure.TerminalReason));
        }
    }
}
