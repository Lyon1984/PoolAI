using System.Diagnostics;
using System.Diagnostics.Metrics;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Operations.Infrastructure.Observability;

internal sealed class OutboxPublisherMetrics : IDisposable
{
    internal const string MeterName = "PoolAI.Operations.Outbox";
    private const int MaximumMeasurementsPerInstrument = 128;
    private static readonly long RefreshTicks = (long)(Stopwatch.Frequency * 5d);
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IOutboxObservabilityStore _observabilityStore;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Meter _meter = new(MeterName);
    private OutboxObservabilitySnapshot _snapshot = OutboxObservabilitySnapshot.Empty;
    private long _nextRefreshTimestamp;

    public OutboxPublisherMetrics(
        IUnitOfWorkFactory unitOfWorkFactory,
        IOutboxObservabilityStore observabilityStore)
    {
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _observabilityStore = observabilityStore
            ?? throw new ArgumentNullException(nameof(observabilityStore));
        _meter.CreateObservableGauge(
            "poolai_outbox_pending",
            ObservePending,
            unit: "{message}",
            description: "Integration Event messages that have not reached a terminal state.");
        _meter.CreateObservableGauge(
            "poolai_outbox_oldest_age_seconds",
            ObserveOldestAge,
            unit: "s",
            description: "Age of the oldest non-terminal Integration Event by event type.");
        _meter.CreateObservableCounter(
            "poolai_outbox_dead_total",
            () => ObserveTerminal(Volatile.Read(ref _snapshot).Dead),
            unit: "{message}",
            description: "Integration Event messages that entered the dead state.");
        _meter.CreateObservableCounter(
            "poolai_outbox_replay_total",
            () => ObserveTerminal(Volatile.Read(ref _snapshot).Replays),
            unit: "{message}",
            description: "Integration Event replay messages created from dead messages.");
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
                OutboxObservabilitySnapshot snapshot = await _observabilityStore
                    .ReadAsync(unitOfWork.Context, cancellationToken)
                    .ConfigureAwait(false);
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

    private IEnumerable<Measurement<long>> ObservePending() =>
        AggregateBacklog(Volatile.Read(ref _snapshot).Backlog)
            .Take(MaximumMeasurementsPerInstrument)
            .Select(static metric => new Measurement<long>(
                metric.PendingCount,
                new KeyValuePair<string, object?>(
                    "event_type",
                    metric.EventType)));

    private IEnumerable<Measurement<double>> ObserveOldestAge() =>
        AggregateBacklog(Volatile.Read(ref _snapshot).Backlog)
            .Take(MaximumMeasurementsPerInstrument)
            .Select(static metric => new Measurement<double>(
                metric.OldestAgeSeconds,
                new KeyValuePair<string, object?>(
                    "event_type",
                    metric.EventType)));

    private static IEnumerable<Measurement<long>> ObserveTerminal(
        IReadOnlyList<OutboxTerminalMetric> metrics) =>
        metrics
            .GroupBy(
                static metric => new TerminalMetricKey(
                    OutboxTelemetryClassifier.NormalizeTopic(metric.Topic),
                    OutboxTelemetryClassifier.NormalizeEventType(metric.EventType),
                    OutboxTelemetryClassifier.NormalizeReason(metric.Reason)))
            .Select(static group => new OutboxTerminalMetric(
                group.Key.Topic,
                group.Key.EventType,
                group.Key.Reason,
                group.Sum(static metric => metric.Count)))
            .OrderBy(static metric => metric.Topic, StringComparer.Ordinal)
            .ThenBy(static metric => metric.EventType, StringComparer.Ordinal)
            .ThenBy(static metric => metric.Reason, StringComparer.Ordinal)
            .Take(MaximumMeasurementsPerInstrument)
            .Select(static metric => new Measurement<long>(
                metric.Count,
                new KeyValuePair<string, object?>("topic", metric.Topic),
                new KeyValuePair<string, object?>(
                    "event_type",
                    metric.EventType),
                new KeyValuePair<string, object?>("reason", metric.Reason)));

    private static IEnumerable<OutboxBacklogMetric> AggregateBacklog(
        IReadOnlyList<OutboxBacklogMetric> metrics) => metrics
        .GroupBy(static metric =>
            OutboxTelemetryClassifier.NormalizeEventType(metric.EventType),
            StringComparer.Ordinal)
        .Select(static group => new OutboxBacklogMetric(
            group.Key,
            group.Sum(static metric => metric.PendingCount),
            group.Max(static metric => metric.OldestAgeSeconds)))
        .OrderBy(static metric => metric.EventType, StringComparer.Ordinal);

    private readonly record struct TerminalMetricKey(
        string Topic,
        string EventType,
        string Reason);
}
