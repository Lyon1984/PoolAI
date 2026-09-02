using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Coordinates the in-process lifetime of one dispatched quota reservation.
/// The database-owned <see cref="ReservationHandle.MaxExpiresAt"/> remains the
/// only persistence hard deadline. A separate request-attempt deadline may end
/// upstream execution sooner without being misclassified as client disconnect.
/// </summary>
public sealed class ReservationLifetimeCoordinator
{
    public static readonly TimeSpan NonStreamRenewInterval =
        TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StreamRenewInterval =
        TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumDrainDuration =
        TimeSpan.FromSeconds(15);

    private readonly IGroupQuotaLedger _quotaLedger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _drainDuration;

    public ReservationLifetimeCoordinator(
        IGroupQuotaLedger quotaLedger,
        TimeProvider timeProvider,
        TimeSpan drainDuration)
    {
        _quotaLedger = quotaLedger
            ?? throw new ArgumentNullException(nameof(quotaLedger));
        _timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        if (drainDuration < TimeSpan.FromSeconds(5)
            || drainDuration > MaximumDrainDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(drainDuration));
        }

        _drainDuration = drainDuration;
    }

    public TimeSpan DrainDuration => _drainDuration;

    public ValueTask<ReservationLifetimeResult> ExecuteAsync(
        DispatchedReservationHandle reservation,
        IReservationLifetimeOperation operation,
        IReservationFinalizationPort finalization,
        CancellationToken clientCancellationToken) => ExecuteAsync(
        reservation,
        operation,
        finalization,
        attemptDeadline: null,
        clientCancellationToken);

    public ValueTask<ReservationLifetimeResult> ExecuteAsync(
        DispatchedReservationHandle reservation,
        IReservationLifetimeOperation operation,
        IReservationFinalizationPort finalization,
        DateTimeOffset attemptDeadline,
        CancellationToken clientCancellationToken) => ExecuteAsync(
        reservation,
        operation,
        finalization,
        (DateTimeOffset?)attemptDeadline,
        clientCancellationToken);

    private ValueTask<ReservationLifetimeResult> ExecuteAsync(
        DispatchedReservationHandle reservation,
        IReservationLifetimeOperation operation,
        IReservationFinalizationPort finalization,
        DateTimeOffset? attemptDeadline,
        CancellationToken clientCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(finalization);

        LifetimeExecution execution = new(
            _quotaLedger,
            _timeProvider,
            reservation,
            operation,
            finalization,
            _drainDuration,
            attemptDeadline,
            clientCancellationToken);
        return execution.RunAsync();
    }

    private sealed class LifetimeExecution : IDisposable
    {
        private readonly IGroupQuotaLedger _quotaLedger;
        private readonly TimeProvider _timeProvider;
        private readonly IReservationFinalizationPort _finalization;
        private readonly ReservationHandle _initialHandle;
        private readonly DateTimeOffset _hardDeadline;
        private readonly TimeSpan _renewInterval;
        private readonly TimeSpan _drainDuration;
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private readonly CancellationTokenSource _upstreamCancellation = new();
        private readonly CancellationTokenSource _drainCancellation = new();
        private readonly CancellationSignal _clientSignal;
        private readonly Task<ReservationSettlementEvidence> _operationTask;
        private readonly Task _hardDeadlineTask;
        private readonly Task? _attemptDeadlineTask;

        private DispatchedReservationHandle _currentReservation;
        private DateTimeOffset _nextRenewAt;
        private Task? _renewDelayTask;
        private Task? _leaseDeadlineTask;
        private Task<Result<ReservationHandle>>? _renewalTask;
        private CancellationTokenSource? _renewalCancellation;
        private Task? _drainDeadlineTask;
        private ReservationSettlementEvidence? _evidence;
        private ReservationLifetimeStopReason _stopReason =
            ReservationLifetimeStopReason.Completed;
        private long _renewalSequence = 1;
        private long _successfulRenewals;
        private bool _drainTimedOut;
        private bool _deadlineObserved;
        private bool _attemptDeadlineObserved;
        private bool _clientObserved;
        private bool _renewalEnabled = true;
        private bool _draining;

        internal LifetimeExecution(
            IGroupQuotaLedger quotaLedger,
            TimeProvider timeProvider,
            DispatchedReservationHandle reservation,
            IReservationLifetimeOperation operation,
            IReservationFinalizationPort finalization,
            TimeSpan drainDuration,
            DateTimeOffset? attemptDeadline,
            CancellationToken clientCancellationToken)
        {
            _quotaLedger = quotaLedger;
            _timeProvider = timeProvider;
            _finalization = finalization;
            _currentReservation = reservation;
            _initialHandle = reservation.Reservation;
            _hardDeadline = _initialHandle.MaxExpiresAt;
            _renewInterval = _initialHandle.IsStreaming
                ? StreamRenewInterval
                : NonStreamRenewInterval;
            _drainDuration = drainDuration;
            _clientSignal = new CancellationSignal(
                _timeProvider,
                clientCancellationToken);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            _hardDeadlineTask = DelayUntilAsync(_hardDeadline);
            _attemptDeadlineTask = attemptDeadline is null
                ? null
                : DelayUntilAsync(attemptDeadline.Value);
            if (TryResolveInitialCompletion(
                    now,
                    attemptDeadline,
                    out Task<ReservationSettlementEvidence> initialCompletion))
            {
                _operationTask = initialCompletion;
                return;
            }

            _operationTask = StartOperation(
                operation,
                new ReservationLifetimeCancellation(
                    _upstreamCancellation.Token,
                    _drainCancellation.Token));
            ScheduleNextRenewal(
                now + _renewInterval,
                _initialHandle.LeaseExpiresAt);
        }

        private bool TryResolveInitialCompletion(
            DateTimeOffset now,
            DateTimeOffset? attemptDeadline,
            out Task<ReservationSettlementEvidence> completion)
        {
            if (now >= _hardDeadline)
            {
                _deadlineObserved = true;
                completion = StopBeforeExecution(
                    ReservationLifetimeStopReason.HardDeadlineReached);
                return true;
            }

            if (attemptDeadline is not null && now >= attemptDeadline.Value)
            {
                _attemptDeadlineObserved = true;
                completion = StopBeforeExecution(
                    ReservationLifetimeStopReason.AttemptDeadlineReached);
                return true;
            }

            if (now >= _initialHandle.LeaseExpiresAt)
            {
                completion = StopBeforeExecution(
                    ReservationLifetimeStopReason.RenewalFailed);
                return true;
            }

            if (_clientSignal.Task.IsCompleted)
            {
                _clientObserved = true;
                completion = StopBeforeExecution(
                    ReservationLifetimeStopReason.ClientDisconnected);
                return true;
            }

            completion = null!;
            return false;
        }

        private Task<ReservationSettlementEvidence> StopBeforeExecution(
            ReservationLifetimeStopReason reason)
        {
            _renewalEnabled = false;
            _draining = true;
            _stopReason = reason;
            return CompletedWithoutExecution();
        }

        internal async ValueTask<ReservationLifetimeResult> RunAsync()
        {
            try
            {
                while (_evidence is null)
                {
                    await AdvanceAsync().ConfigureAwait(false);
                }

                StopLifetime();
                await FinalizeAsync(_evidence).ConfigureAwait(false);
                return new ReservationLifetimeResult(
                    _currentReservation,
                    _stopReason,
                    _successfulRenewals,
                    _drainTimedOut,
                    _evidence is ReservationSettlementEvidence.NoKnownUsage);
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            StopLifetime();
            _renewalCancellation?.Dispose();
            _clientSignal.Dispose();
            _lifetimeCancellation.Dispose();
            _upstreamCancellation.Dispose();
            _drainCancellation.Dispose();
        }

        private async Task AdvanceAsync()
        {
            await Task.WhenAny(BuildPendingTasks()).ConfigureAwait(false);
            if (_operationTask.IsCompleted)
            {
                await CaptureOperationEvidenceAsync().ConfigureAwait(false);
                return;
            }

            if (!_deadlineObserved && _hardDeadlineTask.IsCompleted)
            {
                HandleHardDeadline();
                return;
            }

            if (!_attemptDeadlineObserved
                && _attemptDeadlineTask?.IsCompleted == true)
            {
                HandleAttemptDeadline();
                return;
            }

            if (!_clientObserved && _clientSignal.Task.IsCompleted)
            {
                HandleClientDisconnect();
                return;
            }

            if (_drainDeadlineTask?.IsCompleted == true)
            {
                await HandleDrainTimeoutAsync().ConfigureAwait(false);
                return;
            }

            if (_renewalEnabled && _renewalTask?.IsCompleted == true)
            {
                await CaptureRenewalAsync().ConfigureAwait(false);
                return;
            }

            if (_renewalEnabled && _leaseDeadlineTask?.IsCompleted == true)
            {
                HandleLeaseDeadline();
                return;
            }

            if (_renewalEnabled
                && _renewalTask is null
                && _renewDelayTask?.IsCompleted == true)
            {
                StartRenewal();
            }
        }

        private Task[] BuildPendingTasks()
        {
            List<Task> pending = [_operationTask];
            AddIfNotNull(pending, _deadlineObserved ? null : _hardDeadlineTask);
            AddIfNotNull(
                pending,
                _attemptDeadlineObserved ? null : _attemptDeadlineTask);
            AddIfNotNull(pending, _clientObserved ? null : _clientSignal.Task);
            AddIfNotNull(pending, _renewalEnabled ? _renewDelayTask : null);
            AddIfNotNull(pending, _renewalEnabled ? _renewalTask : null);
            AddIfNotNull(pending, _renewalEnabled ? _leaseDeadlineTask : null);
            AddIfNotNull(pending, _drainDeadlineTask);
            return [.. pending];
        }

        private async Task CaptureOperationEvidenceAsync()
        {
            bool faulted = _operationTask.IsFaulted || _operationTask.IsCanceled;
            _evidence = await ReadOperationEvidenceAsync(_operationTask)
                .ConfigureAwait(false);
            if (_evidence is ReservationSettlementEvidence.NoKnownUsage
                && !_draining)
            {
                _stopReason = faulted
                    ? ReservationLifetimeStopReason.UpstreamFaulted
                    : ReservationLifetimeStopReason
                        .UpstreamCompletedWithoutUsage;
            }
        }

        private void HandleHardDeadline()
        {
            _deadlineObserved = true;
            _renewalEnabled = false;
            CancelRenewal();
            _stopReason = ReservationLifetimeStopReason.HardDeadlineReached;
            BeginDrain(abortUpstream: true);
        }

        private void HandleClientDisconnect()
        {
            _clientObserved = true;
            _stopReason = ReservationLifetimeStopReason.ClientDisconnected;
            BeginDrain(
                abortUpstream: false,
                _clientSignal.SignaledAt + _drainDuration);
        }

        private void HandleAttemptDeadline()
        {
            _attemptDeadlineObserved = true;
            _renewalEnabled = false;
            CancelRenewal();
            _stopReason = ReservationLifetimeStopReason.AttemptDeadlineReached;
            BeginDrain(abortUpstream: true);
        }

        private async Task HandleDrainTimeoutAsync()
        {
            _drainTimedOut = true;
            _renewalEnabled = false;
            CancelRenewal();
            _upstreamCancellation.Cancel();
            _drainCancellation.Cancel();
            if (_operationTask.IsCompleted)
            {
                _evidence = await ReadOperationEvidenceAsync(_operationTask)
                    .ConfigureAwait(false);
                return;
            }

            ObserveFault(_operationTask);
            _evidence = ReservationSettlementEvidence.NoKnownUsage.Instance;
        }

        private async Task CaptureRenewalAsync()
        {
            Result<ReservationHandle>? renewed =
                await ReadRenewalAsync(_renewalTask!).ConfigureAwait(false);
            _renewalCancellation?.Dispose();
            _renewalCancellation = null;
            _renewalTask = null;
            if (renewed is null
                || renewed.IsFailure
                || !IsCompatibleRenewal(renewed.Value))
            {
                _renewalEnabled = false;
                _renewDelayTask = null;
                _stopReason = ReservationLifetimeStopReason.RenewalFailed;
                BeginDrain(abortUpstream: true);
                return;
            }

            _successfulRenewals++;
            _renewalSequence++;
            _currentReservation = _currentReservation with
            {
                Reservation = renewed.Value,
            };
            ScheduleNextRenewal(
                _nextRenewAt + _renewInterval,
                renewed.Value.LeaseExpiresAt);
        }

        private void ScheduleNextRenewal(
            DateTimeOffset cadenceAt,
            DateTimeOffset persistedLeaseExpiresAt)
        {
            _leaseDeadlineTask = DelayUntilAsync(persistedLeaseExpiresAt);
            if (cadenceAt >= _hardDeadline
                && persistedLeaseExpiresAt >= _hardDeadline)
            {
                _renewalEnabled = false;
                _renewDelayTask = null;
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            TimeSpan remainingLease = persistedLeaseExpiresAt - now;
            if (remainingLease <= TimeSpan.Zero)
            {
                _nextRenewAt = now;
            }
            else
            {
                // A locally scheduled cadence must never be allowed to run past
                // the database-owned lease. Half of the persisted remaining
                // lease provides a proportional safety margin when a handle is
                // resumed late, while the normal 60s/30s cadence still wins for
                // a fresh 300s/120s lease.
                long halfRemainingTicks = remainingLease.Ticks / 2;
                DateTimeOffset leaseAnchoredAt = halfRemainingTicks == 0
                    ? now
                    : now.AddTicks(halfRemainingTicks);
                _nextRenewAt = cadenceAt <= leaseAnchoredAt
                    ? cadenceAt
                    : leaseAnchoredAt;
            }

            _renewDelayTask = DelayUntilAsync(_nextRenewAt);
        }

        private void HandleLeaseDeadline()
        {
            DateTimeOffset persistedLeaseExpiresAt =
                _currentReservation.Reservation.LeaseExpiresAt;
            if (_timeProvider.GetUtcNow() < persistedLeaseExpiresAt)
            {
                // Only the task for the current persisted handle is authoritative.
                // A superseded timer can complete after a successful renewal, but
                // it must never revoke the refreshed lease.
                _leaseDeadlineTask = DelayUntilAsync(persistedLeaseExpiresAt);
                return;
            }

            _renewalEnabled = false;
            CancelRenewal();
            _stopReason = ReservationLifetimeStopReason.RenewalFailed;
            BeginDrain(abortUpstream: true);
        }

        private void StartRenewal()
        {
            _renewalCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _lifetimeCancellation.Token);
            _renewalTask = StartRenewalAsync(
                _currentReservation.Reservation,
                _renewalSequence,
                _renewalCancellation.Token);
            _renewDelayTask = null;
        }

        private Task<Result<ReservationHandle>> StartRenewalAsync(
            ReservationHandle reservation,
            long renewalSequence,
            CancellationToken cancellationToken)
        {
            try
            {
                return _quotaLedger.RenewAsync(
                        new RenewReservationCommand(
                            reservation,
                            renewalSequence),
                        cancellationToken)
                    .AsTask();
            }
            catch (Exception exception)
            {
                return Task.FromException<Result<ReservationHandle>>(exception);
            }
        }

        private void BeginDrain(
            bool abortUpstream,
            DateTimeOffset? deadline = null)
        {
            if (!_draining)
            {
                _draining = true;
                _drainDeadlineTask = deadline is null
                    ? DelayAsync(_drainDuration)
                    : DelayUntilAsync(deadline.Value);
            }

            if (abortUpstream)
            {
                _upstreamCancellation.Cancel();
            }
        }

        private async Task FinalizeAsync(ReservationSettlementEvidence evidence)
        {
            if (evidence is ReservationSettlementEvidence.KnownUsage knownUsage)
            {
                await _finalization.SettleKnownUsageAsync(
                        _currentReservation,
                        knownUsage,
                        _stopReason,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            await _finalization.SettleConservativelyAsync(
                    _currentReservation,
                    new ConservativeReservationSettlement(
                        _stopReason,
                        _drainTimedOut),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        private Task DelayUntilAsync(DateTimeOffset deadline)
        {
            TimeSpan delay = deadline - _timeProvider.GetUtcNow();
            return delay <= TimeSpan.Zero
                ? Task.CompletedTask
                : DelayAsync(delay);
        }

        private Task DelayAsync(TimeSpan delay) =>
            Task.Delay(delay, _timeProvider, _lifetimeCancellation.Token);

        private bool IsCompatibleRenewal(ReservationHandle renewed) =>
            renewed.ReservationId == _initialHandle.ReservationId
            && renewed.RequestId == _initialHandle.RequestId
            && renewed.AttemptId == _initialHandle.AttemptId
            && renewed.AttemptIndex == _initialHandle.AttemptIndex
            && renewed.GroupId == _initialHandle.GroupId
            && renewed.PeriodId == _initialHandle.PeriodId
            && renewed.AccountId == _initialHandle.AccountId
            && renewed.ChannelId == _initialHandle.ChannelId
            && renewed.EstimatedTokens == _initialHandle.EstimatedTokens
            && renewed.IsStreaming == _initialHandle.IsStreaming
            && string.Equals(
                renewed.LeaseOwner,
                _initialHandle.LeaseOwner,
                StringComparison.Ordinal)
            && renewed.MaxExpiresAt == _hardDeadline
            && renewed.LeaseExpiresAt
                >= _currentReservation.Reservation.LeaseExpiresAt
            && renewed.LeaseExpiresAt <= _hardDeadline;

        private void CancelRenewal()
        {
            _renewDelayTask = null;
            _leaseDeadlineTask = null;
            _renewalCancellation?.Cancel();
            _renewalCancellation?.Dispose();
            _renewalCancellation = null;
            if (_renewalTask is not null)
            {
                ObserveFault(_renewalTask);
                _renewalTask = null;
            }
        }

        private void StopLifetime()
        {
            _lifetimeCancellation.Cancel();
            CancelRenewal();
            _upstreamCancellation.Cancel();
            _drainCancellation.Cancel();
        }

        private static Task<ReservationSettlementEvidence> StartOperation(
            IReservationLifetimeOperation operation,
            ReservationLifetimeCancellation cancellation)
        {
            try
            {
                return operation.ExecuteAsync(cancellation).AsTask();
            }
            catch (Exception exception)
            {
                return Task.FromException<ReservationSettlementEvidence>(exception);
            }
        }

        private static Task<ReservationSettlementEvidence>
            CompletedWithoutExecution() =>
            Task.FromResult<ReservationSettlementEvidence>(
                new ReservationSettlementEvidence.KnownUsage(
                    new TokenUsage(0, 0, 0, 0, 0),
                    SettlementUsageSource.ConfirmedNoExecution));

        private static async Task<ReservationSettlementEvidence>
            ReadOperationEvidenceAsync(
                Task<ReservationSettlementEvidence> operationTask)
        {
            try
            {
                return await operationTask.ConfigureAwait(false)
                    ?? ReservationSettlementEvidence.NoKnownUsage.Instance;
            }
            catch (Exception)
            {
                return ReservationSettlementEvidence.NoKnownUsage.Instance;
            }
        }

        private static async Task<Result<ReservationHandle>?> ReadRenewalAsync(
            Task<Result<ReservationHandle>> renewalTask)
        {
            try
            {
                return await renewalTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AddIfNotNull(List<Task> tasks, Task? task)
        {
            if (task is not null)
            {
                tasks.Add(task);
            }
        }

        private static void ObserveFault(Task task) =>
            _ = task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private sealed class CancellationSignal : IDisposable
    {
        private const long UnsetUtcTicks = long.MinValue;

        private readonly CancellationTokenRegistration _registration;
        private readonly TimeProvider _timeProvider;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _signaledUtcTicks = UnsetUtcTicks;

        internal CancellationSignal(
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            _timeProvider = timeProvider;
            _registration = cancellationToken.UnsafeRegister(
                static state => ((CancellationSignal)state!).Signal(),
                this);
        }

        internal Task Task => _completion.Task;

        internal DateTimeOffset SignaledAt
        {
            get
            {
                long ticks = Interlocked.Read(ref _signaledUtcTicks);
                if (ticks == UnsetUtcTicks)
                {
                    throw new InvalidOperationException(
                        "Client cancellation has not been observed.");
                }

                return new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public void Dispose() => _registration.Dispose();

        private void Signal()
        {
            _ = Interlocked.CompareExchange(
                ref _signaledUtcTicks,
                _timeProvider.GetUtcNow().UtcTicks,
                UnsetUtcTicks);
            _completion.TrySetResult();
        }
    }
}
