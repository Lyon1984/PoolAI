using System.Diagnostics.Metrics;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayAdmissionMetrics : IDisposable
{
    public const string MeterName = "PoolAI.Gateway";
    public const string ActiveInstrumentName = "poolai_admission_active";
    public const string RejectedInstrumentName = "poolai_admission_rejected_total";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly ObservableGauge<long> _active;
    private readonly Counter<long> _rejected;
    private readonly long[] _activeByKind = new long[4];

    public GatewayAdmissionMetrics()
        : this(new Meter(MeterName, "1.0"), ownsMeter: true)
    {
    }

    public GatewayAdmissionMetrics(Meter meter)
        : this(meter, ownsMeter: false)
    {
    }

    private GatewayAdmissionMetrics(Meter meter, bool ownsMeter)
    {
        _meter = meter ?? throw new ArgumentNullException(nameof(meter));
        _ownsMeter = ownsMeter;
        _active = _meter.CreateObservableGauge(
            ActiveInstrumentName,
            ObserveActive,
            unit: "{request}",
            description: "Requests currently holding a process-local admission permit.");
        _rejected = _meter.CreateCounter<long>(
            RejectedInstrumentName,
            unit: "{request}",
            description: "Requests rejected by a process-local admission partition.");
    }

    internal void ChangeActive(GatewayAdmissionKind kind, long delta) =>
        Interlocked.Add(ref _activeByKind[ToIndex(kind)], delta);

    internal void RecordRejection(
        GatewayAdmissionKind kind,
        GatewayAdmissionRejectionOutcome outcome) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("bulkhead", Label(kind)),
            new KeyValuePair<string, object?>("outcome", OutcomeLabel(outcome)));

    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    private IEnumerable<Measurement<long>> ObserveActive()
    {
        foreach (GatewayAdmissionKind kind in Enum.GetValues<GatewayAdmissionKind>())
        {
            yield return new Measurement<long>(
                Interlocked.Read(ref _activeByKind[ToIndex(kind)]),
                new KeyValuePair<string, object?>("bulkhead", Label(kind)),
                new KeyValuePair<string, object?>("outcome", "active"));
        }
    }

    private static int ToIndex(GatewayAdmissionKind kind) => kind switch
    {
        GatewayAdmissionKind.NonStream => 0,
        GatewayAdmissionKind.Sse => 1,
        GatewayAdmissionKind.Control => 2,
        GatewayAdmissionKind.Usage => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string Label(GatewayAdmissionKind kind) => kind switch
    {
        GatewayAdmissionKind.NonStream => "data-nonstream",
        GatewayAdmissionKind.Sse => "data-stream",
        GatewayAdmissionKind.Control => "control",
        GatewayAdmissionKind.Usage => "usage",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string OutcomeLabel(GatewayAdmissionRejectionOutcome outcome) =>
        outcome switch
        {
            GatewayAdmissionRejectionOutcome.Capacity => "capacity_exhausted",
            GatewayAdmissionRejectionOutcome.WaitBudget => "wait_budget_exhausted",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
}
