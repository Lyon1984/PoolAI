namespace PoolAI.Modules.Gateway.Abstractions;

/// <summary>
/// Read-only Adapter view of one live Gateway-owned attempt. PostgreSQL owns
/// the dispatch fence; lifecycle and output evidence can only be advanced by
/// the Gateway assembly through the internal evidence sink.
/// </summary>
public sealed class AdapterAttemptContext
{
    private int _phase = (int)GatewayAttemptPhase.Prepared;
    private int _requestBytesWritten;
    private readonly IGatewayAttemptOutputEvidenceSink? _outputEvidenceSink;

    internal AdapterAttemptContext(
        EntityId requestId,
        EntityId attemptId,
        int attemptIndex,
        AdapterRouteSnapshot route,
        DateTimeOffset deadline,
        int remainingRetryBudget)
        : this(
            requestId,
            attemptId,
            attemptIndex,
            route,
            deadline,
            remainingRetryBudget,
            outputEvidenceSink: null)
    {
    }

    internal AdapterAttemptContext(
        EntityId requestId,
        EntityId attemptId,
        int attemptIndex,
        AdapterRouteSnapshot route,
        DateTimeOffset deadline,
        int remainingRetryBudget,
        IGatewayAttemptOutputEvidenceSink? outputEvidenceSink)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (requestId.Value.Version != 7
            || attemptId.Value.Version != 7
            || attemptIndex < 0
            || deadline == default
            || remainingRetryBudget < 0)
        {
            throw new ArgumentException(
                "The Adapter attempt context is invalid.",
                nameof(requestId));
        }

        RequestId = requestId;
        AttemptId = attemptId;
        AttemptIndex = attemptIndex;
        Route = route;
        Deadline = deadline;
        RemainingRetryBudget = remainingRetryBudget;
        _outputEvidenceSink = outputEvidenceSink;
    }

    public EntityId RequestId { get; }

    public EntityId AttemptId { get; }

    public int AttemptIndex { get; }

    public AdapterRouteSnapshot Route { get; }

    public DateTimeOffset Deadline { get; }

    public int RemainingRetryBudget { get; }

    public GatewayAttemptPhase Phase =>
        (GatewayAttemptPhase)Volatile.Read(ref _phase);

    public bool RequestBytesWritten =>
        Volatile.Read(ref _requestBytesWritten) != 0;

    internal IGatewayAttemptOutputEvidenceSink OutputEvidenceSink =>
        _outputEvidenceSink
        ?? throw new InvalidOperationException(
            "The Adapter attempt has no Gateway output evidence owner.");

    internal void MarkDispatchedAfterFence() => Advance(
        GatewayAttemptPhase.Prepared,
        GatewayAttemptPhase.DispatchedNoDownstreamHeaders);

    internal void MarkRequestBytesWritten()
    {
        if (Phase < GatewayAttemptPhase.DispatchedNoDownstreamHeaders)
        {
            throw new InvalidOperationException(
                "Upstream bytes cannot be written before the dispatch fence commits.");
        }

        Interlocked.Exchange(ref _requestBytesWritten, 1);
    }

    internal void AdvanceToDownstreamHeadersCommitted() => Advance(
        GatewayAttemptPhase.DispatchedNoDownstreamHeaders,
        GatewayAttemptPhase.DownstreamHeadersCommitted);

    internal void AdvanceToBusinessOutputStarted() => Advance(
        GatewayAttemptPhase.DownstreamHeadersCommitted,
        GatewayAttemptPhase.BusinessOutputStarted);

    public override string ToString() => nameof(AdapterAttemptContext);

    private void Advance(
        GatewayAttemptPhase expected,
        GatewayAttemptPhase next)
    {
        int observed = Interlocked.CompareExchange(
            ref _phase,
            (int)next,
            (int)expected);
        if (observed != (int)expected && observed != (int)next)
        {
            throw new InvalidOperationException(
                "The Gateway attempt phase transition is not monotonic.");
        }
    }
}
