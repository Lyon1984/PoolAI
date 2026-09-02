using System.Threading.RateLimiting;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Owns the four process-local admission partitions frozen by AC-043. Each
/// partition has its own permit count and FIFO queue; no Redis or database
/// state participates in this pre-canonical-read boundary.
/// </summary>
public sealed class GatewayAdmissionController : IDisposable
{
    private const int RetryAfterSeconds = 1;
    private readonly AdmissionPartition _nonStream;
    private readonly AdmissionPartition _sse;
    private readonly AdmissionPartition _control;
    private readonly AdmissionPartition _usage;
    private readonly GatewayAdmissionMetrics _metrics;
    private readonly bool _ownsMetrics;

    public GatewayAdmissionController(
        GatewayAdmissionOptions options,
        GatewayAdmissionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ownsMetrics = metrics is null;
        _metrics = metrics ?? new GatewayAdmissionMetrics();
        _nonStream = new AdmissionPartition(
            options.DataNonStreamPermits,
            options.DataQueueLimit,
            _metrics);
        _sse = new AdmissionPartition(
            options.DataStreamPermits,
            options.DataQueueLimit,
            _metrics);
        _control = new AdmissionPartition(
            options.ControlPermits,
            options.ControlQueueLimit,
            _metrics);
        _usage = new AdmissionPartition(
            options.UsagePermits,
            options.UsageQueueLimit,
            _metrics);
    }

    public ValueTask<Result<GatewayAdmissionLease>> AcquireAsync(
        GatewayAdmissionKind kind,
        CancellationToken cancellationToken = default) =>
        SelectPartition(kind).AcquireAsync(
            kind,
            cancellationToken,
            CancellationToken.None);

    /// <summary>
    /// Acquires a permit while keeping client disconnect cancellation distinct
    /// from the server-owned queue deadline. A client disconnect is propagated
    /// as cancellation; only the server deadline maps to gateway_overloaded.
    /// </summary>
    public ValueTask<Result<GatewayAdmissionLease>> AcquireAsync(
        GatewayAdmissionKind kind,
        CancellationToken clientCancellationToken,
        CancellationToken serverWaitCancellationToken) =>
        SelectPartition(kind).AcquireAsync(
            kind,
            clientCancellationToken,
            serverWaitCancellationToken);

    public async ValueTask<Result<T>> ExecuteAsync<T>(
        GatewayAdmissionKind kind,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Result<GatewayAdmissionLease> acquired = await AcquireAsync(
                kind,
                cancellationToken)
            .ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            ResultError error = acquired.Error;
            return Result.Failure<T>(
                error.Code,
                error.Description,
                error.RetryAfterSeconds,
                error.ETag,
                error.Presentation);
        }

        using GatewayAdmissionLease lease = acquired.Value;
        return Result.Success(
            await operation(cancellationToken).ConfigureAwait(false));
    }

    public void Dispose()
    {
        _nonStream.Dispose();
        _sse.Dispose();
        _control.Dispose();
        _usage.Dispose();
        if (_ownsMetrics)
        {
            _metrics.Dispose();
        }
    }

    private AdmissionPartition SelectPartition(GatewayAdmissionKind kind) =>
        kind switch
        {
            GatewayAdmissionKind.NonStream => _nonStream,
            GatewayAdmissionKind.Sse => _sse,
            GatewayAdmissionKind.Control => _control,
            GatewayAdmissionKind.Usage => _usage,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static Result<GatewayAdmissionLease> Overloaded() =>
        Result.Failure<GatewayAdmissionLease>(
            ErrorCodesV1.GatewayOverloaded,
            "The selected local admission bulkhead is saturated.",
            retryAfterSeconds: RetryAfterSeconds,
            presentation: new ResultErrorPresentation(
                ErrorCodesV1.GatewayOverloaded,
                429,
                "Gateway overloaded",
                "The selected request partition has no available capacity.",
                Retryable: true,
                RetryAfterSeconds: RetryAfterSeconds));

    private sealed class AdmissionPartition : IDisposable
    {
        private readonly ConcurrencyLimiter _limiter;
        private readonly GatewayAdmissionMetrics _metrics;

        internal AdmissionPartition(
            int permitLimit,
            int queueLimit,
            GatewayAdmissionMetrics metrics)
        {
            _metrics = metrics;
            _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = permitLimit,
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            });
        }

        internal async ValueTask<Result<GatewayAdmissionLease>> AcquireAsync(
            GatewayAdmissionKind kind,
            CancellationToken clientCancellationToken,
            CancellationToken serverWaitCancellationToken)
        {
            clientCancellationToken.ThrowIfCancellationRequested();
            if (serverWaitCancellationToken.IsCancellationRequested)
            {
                clientCancellationToken.ThrowIfCancellationRequested();
                _metrics.RecordRejection(kind, GatewayAdmissionRejectionOutcome.WaitBudget);
                return Overloaded();
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    clientCancellationToken,
                    serverWaitCancellationToken);
            RateLimitLease? acquired = await TryAcquireAsync(
                    clientCancellationToken,
                    serverWaitCancellationToken,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (acquired is null)
            {
                clientCancellationToken.ThrowIfCancellationRequested();
                _metrics.RecordRejection(kind, GatewayAdmissionRejectionOutcome.WaitBudget);
                return Overloaded();
            }

            // A client disconnect always wins an acquisition/cancellation race;
            // never turn that race into a synthetic overload response.
            if (clientCancellationToken.IsCancellationRequested)
            {
                acquired.Dispose();
                throw new OperationCanceledException(clientCancellationToken);
            }

            if (serverWaitCancellationToken.IsCancellationRequested)
            {
                acquired.Dispose();
                _metrics.RecordRejection(kind, GatewayAdmissionRejectionOutcome.WaitBudget);
                return Overloaded();
            }

            if (!acquired.IsAcquired)
            {
                acquired.Dispose();
                _metrics.RecordRejection(kind, GatewayAdmissionRejectionOutcome.Capacity);
                return Overloaded();
            }

            _metrics.ChangeActive(kind, 1);
            return Result.Success(new GatewayAdmissionLease(
                kind,
                () =>
                {
                    _metrics.ChangeActive(kind, -1);
                    acquired.Dispose();
                }));
        }

        public void Dispose() => _limiter.Dispose();

        private async ValueTask<RateLimitLease?> TryAcquireAsync(
            CancellationToken clientCancellationToken,
            CancellationToken serverWaitCancellationToken,
            CancellationToken linkedCancellationToken)
        {
            try
            {
                return await _limiter.AcquireAsync(
                        permitCount: 1,
                        linkedCancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (clientCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(clientCancellationToken);
            }
            catch (OperationCanceledException)
                when (serverWaitCancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }
}
