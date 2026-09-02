using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/开发执行规格-v1.0.md, DEC-036, AC-036 and M3-E3.
// - docs/architecture/design-pattern-baseline.md, Gateway Process Manager.
public sealed class M3E3ReservationLifetimeCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        1,
        2,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task NonStreamRenewsEverySixtySecondsAndStopsAtSixHundredWithBoundedDrain()
    {
        CoordinatorHarness harness = new(isStreaming: false);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);

        for (int expected = 1; expected <= 9; expected++)
        {
            harness.Time.Advance(
                ReservationLifetimeCoordinator.NonStreamRenewInterval);
            RenewReservationCommand renewal =
                await harness.Ledger.NextRenewalAsync();
            Assert.Equal(expected, renewal.RenewalSequence);
            await PumpAsync();
        }

        harness.Time.Advance(
            ReservationLifetimeCoordinator.NonStreamRenewInterval);
        await harness.Operation.Aborted;

        Assert.Equal(9, harness.Ledger.RenewCommands.Count);
        Assert.False(execution.IsCompleted);
        Assert.False(harness.Operation.Drain.IsCancellationRequested);

        harness.Time.Advance(TimeSpan.FromSeconds(14));
        await PumpAsync();
        Assert.False(execution.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        ReservationLifetimeResult result = await execution;
        Assert.Equal(
            ReservationLifetimeStopReason.HardDeadlineReached,
            result.StopReason);
        Assert.Equal(9, result.SuccessfulRenewals);
        Assert.True(result.DrainTimedOut);
        Assert.True(result.SettledConservatively);
        Assert.Equal(0, harness.Finalization.KnownUsageCalls);
        Assert.Equal(1, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task StreamRenewsEveryThirtySeconds()
    {
        CoordinatorHarness harness = new(isStreaming: true);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);

        harness.Time.Advance(TimeSpan.FromSeconds(29));
        await PumpAsync();
        Assert.Empty(harness.Ledger.RenewCommands);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        RenewReservationCommand firstRenewal =
            await harness.Ledger.NextRenewalAsync();
        Assert.Equal(1, firstRenewal.RenewalSequence);
        await PumpAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(29));
        await PumpAsync();
        Assert.Single(harness.Ledger.RenewCommands);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        RenewReservationCommand secondRenewal =
            await harness.Ledger.NextRenewalAsync();
        Assert.Equal(2, secondRenewal.RenewalSequence);
        await PumpAsync();

        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;
        Assert.Equal(2, result.SuccessfulRenewals);
        Assert.Equal(
            ReservationLifetimeStopReason.Completed,
            result.StopReason);
        Assert.False(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.KnownUsageCalls);
        Assert.Equal(0, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task FirstRenewalIsAnchoredBeforeAShortPersistedLeaseExpires()
    {
        DateTimeOffset leaseExpiresAt = Now.AddSeconds(20);
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: leaseExpiresAt);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);

        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await PumpAsync();
        Assert.Empty(harness.Ledger.RenewCommands);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        RenewReservationCommand renewal =
            await harness.Ledger.NextRenewalAsync();
        Assert.Equal(1, renewal.RenewalSequence);
        Assert.True(harness.Time.GetUtcNow() < leaseExpiresAt);
        await PumpAsync();

        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;
        Assert.Equal(1, result.SuccessfulRenewals);
        Assert.False(result.SettledConservatively);
    }

    [Fact]
    public async Task StreamStopsAtSevenThousandTwoHundredSeconds()
    {
        CoordinatorHarness harness = new(isStreaming: true);
        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();

        for (int expected = 1; expected <= 239; expected++)
        {
            harness.Time.Advance(
                ReservationLifetimeCoordinator.StreamRenewInterval);
            RenewReservationCommand renewal =
                await harness.Ledger.NextRenewalAsync();
            Assert.Equal(expected, renewal.RenewalSequence);
            await PumpAsync();
        }

        harness.Time.Advance(ReservationLifetimeCoordinator.StreamRenewInterval);
        await harness.Operation.Aborted;
        Assert.Equal(239, harness.Ledger.RenewCommands.Count);

        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;
        Assert.Equal(
            ReservationLifetimeStopReason.HardDeadlineReached,
            result.StopReason);
        Assert.Equal(239, result.SuccessfulRenewals);
        Assert.True(result.DrainTimedOut);
        Assert.True(result.SettledConservatively);
    }

    [Fact]
    public async Task RenewalFailureCancelsUpstreamAndUsesBoundedDrain()
    {
        CoordinatorHarness harness = new(isStreaming: false);
        harness.Ledger.FailRenewalAtSequence = 1;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);
        harness.Time.Advance(
            ReservationLifetimeCoordinator.NonStreamRenewInterval);
        await harness.Operation.Aborted;

        Assert.Single(harness.Ledger.RenewCommands);
        Assert.False(harness.Operation.Drain.IsCancellationRequested);
        Assert.False(execution.IsCompleted);

        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;
        Assert.Equal(
            ReservationLifetimeStopReason.RenewalFailed,
            result.StopReason);
        Assert.Equal(0, result.SuccessfulRenewals);
        Assert.True(result.DrainTimedOut);
        Assert.True(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task PendingRenewalIsCancelledAtPersistedLeaseExpiryAndUsesBoundedDrain()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now.AddSeconds(20));
        harness.Ledger.PendingRenewalAtSequence = 1;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        RenewReservationCommand renewal =
            await harness.Ledger.NextRenewalAsync();
        Assert.Equal(1, renewal.RenewalSequence);

        harness.Time.Advance(TimeSpan.FromSeconds(9));
        await PumpAsync();
        Assert.False(harness.Operation.Aborted.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Operation.Aborted;
        await harness.Ledger.PendingRenewalCancellationObserved;

        Assert.False(execution.IsCompleted);
        Assert.False(harness.Operation.Drain.IsCancellationRequested);
        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;

        Assert.Equal(
            ReservationLifetimeStopReason.RenewalFailed,
            result.StopReason);
        Assert.Equal(0, result.SuccessfulRenewals);
        Assert.True(result.DrainTimedOut);
        Assert.True(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task RenewalCompletedAtLastSafeSecondReplacesOldLeaseDeadline()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now.AddSeconds(20));
        harness.Ledger.PendingRenewalAtSequence = 1;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        _ = await harness.Ledger.NextRenewalAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(9));
        harness.Ledger.SucceedPendingRenewal(Now.AddMinutes(2));
        await PumpAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await PumpAsync();

        Assert.False(harness.Operation.Aborted.IsCompleted);
        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;

        Assert.Equal(1, result.SuccessfulRenewals);
        Assert.Equal(ReservationLifetimeStopReason.Completed, result.StopReason);
        Assert.False(result.SettledConservatively);
    }

    [Fact]
    public async Task RefreshedLeaseSupersedesOldLeaseWatchdog()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now.AddSeconds(20));
        harness.Ledger.PendingRenewalAtSequence = 1;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        _ = await harness.Ledger.NextRenewalAsync();
        harness.Ledger.SucceedPendingRenewal(Now.AddMinutes(2));
        await PumpAsync();

        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await PumpAsync();
        Assert.False(harness.Operation.Aborted.IsCompleted);
        Assert.False(execution.IsCompleted);

        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;
        Assert.Equal(1, result.SuccessfulRenewals);
        Assert.False(result.SettledConservatively);
    }

    [Fact]
    public async Task LateRenewalSuccessCannotResurrectAnExpiredLease()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now.AddSeconds(20));
        harness.Ledger.PendingRenewalAtSequence = 1;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        _ = await harness.Ledger.NextRenewalAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await harness.Operation.Aborted;

        harness.Ledger.SucceedPendingRenewal(Now.AddMinutes(2));
        await PumpAsync();
        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;

        Assert.Equal(
            ReservationLifetimeStopReason.RenewalFailed,
            result.StopReason);
        Assert.Equal(0, result.SuccessfulRenewals);
        Assert.True(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.TotalCalls);
    }

    [Fact]
    public async Task RenewalResultCannotRegressTheDatabaseLease()
    {
        CoordinatorHarness harness = new(isStreaming: false);
        harness.Ledger.RegressLease = true;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);
        harness.Time.Advance(
            ReservationLifetimeCoordinator.NonStreamRenewInterval);
        await harness.Operation.Aborted;

        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;

        Assert.Equal(ReservationLifetimeStopReason.RenewalFailed, result.StopReason);
        Assert.Equal(0, result.SuccessfulRenewals);
        Assert.True(result.SettledConservatively);
    }

    [Fact]
    public async Task ClientCancellationKeepsIndependentDrainAndAllowsRenewalAndKnownUsage()
    {
        CoordinatorHarness harness = new(isStreaming: false);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);
        harness.Time.Advance(TimeSpan.FromSeconds(55));
        await PumpAsync();

        harness.ClientCancellation.Cancel();
        await PumpAsync();

        Assert.False(harness.Operation.Aborted.IsCompleted);
        Assert.False(harness.Operation.Drain.IsCancellationRequested);
        Assert.False(execution.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        RenewReservationCommand renewal =
            await harness.Ledger.NextRenewalAsync();
        Assert.Equal(1, renewal.RenewalSequence);
        await PumpAsync();
        Assert.False(harness.Operation.Drain.IsCancellationRequested);

        harness.Time.Advance(TimeSpan.FromSeconds(5));
        await PumpAsync();
        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;
        Assert.Equal(
            ReservationLifetimeStopReason.ClientDisconnected,
            result.StopReason);
        Assert.Equal(1, result.SuccessfulRenewals);
        Assert.False(result.DrainTimedOut);
        Assert.False(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.KnownUsageCalls);
        Assert.Equal(0, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task ClientDisconnectDrainNeverExceedsFifteenSeconds()
    {
        CoordinatorHarness harness = new(isStreaming: true);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);
        harness.ClientCancellation.Cancel();
        await PumpAsync();
        Assert.False(harness.Operation.Aborted.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(14));
        await PumpAsync();
        Assert.False(execution.IsCompleted);
        Assert.False(harness.Operation.Drain.IsCancellationRequested);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        ReservationLifetimeResult result = await execution;
        Assert.True(harness.Operation.Aborted.IsCompleted);
        Assert.True(result.DrainTimedOut);
        Assert.True(harness.Operation.Drain.IsCancellationRequested);
        Assert.Equal(1, harness.Finalization.ConservativeCalls);
    }

    [Fact]
    public async Task ClientDisconnectHonorsAConfiguredFiveSecondDrain()
    {
        CoordinatorHarness harness = new(
            isStreaming: true,
            drainDuration: TimeSpan.FromSeconds(5));

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.ClientCancellation.Cancel();
        await PumpAsync();
        Assert.False(harness.Operation.Aborted.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(4));
        await PumpAsync();
        Assert.False(execution.IsCompleted);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        ReservationLifetimeResult result = await execution;
        Assert.True(harness.Operation.Aborted.IsCompleted);
        Assert.True(result.DrainTimedOut);
        Assert.True(harness.Operation.Drain.IsCancellationRequested);
    }

    [Fact]
    public async Task DrainTimeoutPreservesUsageCompletedByCancellationCallback()
    {
        CoordinatorHarness harness = new(isStreaming: true);
        harness.Operation.CompleteKnownUsageWhenDrainIsCanceled = true;

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        harness.ClientCancellation.Cancel();
        await PumpAsync();

        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        ReservationLifetimeResult result = await execution;

        Assert.True(result.DrainTimedOut);
        Assert.False(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.KnownUsageCalls);
        Assert.Equal(0, harness.Finalization.ConservativeCalls);
        Assert.Equal(
            SettlementUsageSource.Upstream,
            harness.Finalization.LastKnownUsage?.UsageSource);
    }

    [Fact]
    public async Task PreCanceledClientDoesNotStartAnUpstreamOperation()
    {
        CoordinatorHarness harness = new(isStreaming: false);
        harness.ClientCancellation.Cancel();

        ReservationLifetimeResult result = await harness.ExecuteAsync();

        Assert.False(harness.Operation.Started);
        Assert.Empty(harness.Ledger.RenewCommands);
        Assert.Equal(
            ReservationLifetimeStopReason.ClientDisconnected,
            result.StopReason);
        AssertConfirmedNoExecution(harness, result);
    }

    [Fact]
    public async Task ExpiredPersistedLeaseWithFutureMaximumDoesNotStartOperation()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now);

        ReservationLifetimeResult result = await harness.ExecuteAsync();

        Assert.False(harness.Operation.Started);
        Assert.Empty(harness.Ledger.RenewCommands);
        Assert.Equal(
            ReservationLifetimeStopReason.RenewalFailed,
            result.StopReason);
        AssertConfirmedNoExecution(harness, result);
    }

    [Fact]
    public async Task ExpiredPersistedMaximumDoesNotStartAnUpstreamOperation()
    {
        CoordinatorHarness harness = new(
            isStreaming: false,
            leaseExpiresAt: Now,
            maxExpiresAt: Now);

        ReservationLifetimeResult result = await harness.ExecuteAsync();

        Assert.False(harness.Operation.Started);
        Assert.Empty(harness.Ledger.RenewCommands);
        Assert.Equal(
            ReservationLifetimeStopReason.HardDeadlineReached,
            result.StopReason);
        AssertConfirmedNoExecution(harness, result);
    }

    [Fact]
    public async Task CompletedCallFinalizesExactlyOnce()
    {
        CoordinatorHarness harness = new(isStreaming: false);

        Task<ReservationLifetimeResult> execution = harness.ExecuteAsync();
        Assert.True(harness.Operation.Started);
        harness.Operation.CompleteKnownUsage();
        ReservationLifetimeResult result = await execution;
        harness.ClientCancellation.Cancel();
        harness.Time.Advance(TimeSpan.FromHours(3));
        await PumpAsync();

        Assert.False(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.KnownUsageCalls);
        Assert.Equal(0, harness.Finalization.ConservativeCalls);
        Assert.Equal(1, harness.Finalization.TotalCalls);
    }

    private static async Task PumpAsync()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            await Task.Yield();
        }
    }

    private static void AssertConfirmedNoExecution(
        CoordinatorHarness harness,
        ReservationLifetimeResult result)
    {
        Assert.False(result.SettledConservatively);
        Assert.Equal(1, harness.Finalization.KnownUsageCalls);
        Assert.Equal(0, harness.Finalization.ConservativeCalls);
        Assert.Equal(
            SettlementUsageSource.ConfirmedNoExecution,
            harness.Finalization.LastKnownUsage?.UsageSource);
        Assert.Equal(
            new TokenUsage(0, 0, 0, 0, 0),
            harness.Finalization.LastKnownUsage?.Usage);
    }

    private sealed class CoordinatorHarness
    {
        internal CoordinatorHarness(
            bool isStreaming,
            TimeSpan? drainDuration = null,
            DateTimeOffset? leaseExpiresAt = null,
            DateTimeOffset? maxExpiresAt = null)
        {
            Time = new FakeTimeProvider(Now);
            Ledger = new ScriptedQuotaLedger(Time);
            Coordinator = new ReservationLifetimeCoordinator(
                Ledger,
                Time,
                drainDuration
                    ?? ReservationLifetimeCoordinator.MaximumDrainDuration);
            Operation = new ScriptedOperation();
            Finalization = new RecordingFinalizationPort();
            ClientCancellation = new CancellationTokenSource();
            Reservation = CreateReservation(isStreaming);
            if (leaseExpiresAt is not null || maxExpiresAt is not null)
            {
                Reservation = Reservation with
                {
                    Reservation = Reservation.Reservation with
                    {
                        LeaseExpiresAt = leaseExpiresAt
                            ?? Reservation.Reservation.LeaseExpiresAt,
                        MaxExpiresAt = maxExpiresAt
                            ?? Reservation.Reservation.MaxExpiresAt,
                    },
                };
            }
        }

        internal FakeTimeProvider Time { get; }

        internal ScriptedQuotaLedger Ledger { get; }

        internal ReservationLifetimeCoordinator Coordinator { get; }

        internal ScriptedOperation Operation { get; }

        internal RecordingFinalizationPort Finalization { get; }

        internal CancellationTokenSource ClientCancellation { get; }

        internal DispatchedReservationHandle Reservation { get; }

        internal Task<ReservationLifetimeResult> ExecuteAsync() =>
            Coordinator.ExecuteAsync(
                    Reservation,
                    Operation,
                    Finalization,
                    ClientCancellation.Token)
                .AsTask();
    }

    private sealed class ScriptedOperation : IReservationLifetimeOperation
    {
        private readonly TaskCompletionSource _aborted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ReservationSettlementEvidence>
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Started { get; private set; }

        internal CancellationToken Drain { get; private set; }

        internal Task Aborted => _aborted.Task;

        internal bool CompleteKnownUsageWhenDrainIsCanceled { get; set; }

        public ValueTask<ReservationSettlementEvidence> ExecuteAsync(
            ReservationLifetimeCancellation cancellation)
        {
            Started = true;
            Drain = cancellation.Drain;
            _ = cancellation.AbortUpstream.UnsafeRegister(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _aborted);
            if (CompleteKnownUsageWhenDrainIsCanceled)
            {
                _ = cancellation.Drain.UnsafeRegister(
                    static state =>
                        ((ScriptedOperation)state!).CompleteKnownUsage(),
                    this);
            }

            return new ValueTask<ReservationSettlementEvidence>(_completion.Task);
        }

        internal void CompleteKnownUsage() =>
            _completion.TrySetResult(
                new ReservationSettlementEvidence.KnownUsage(
                    new TokenUsage(11, 7, 0, 0, 0),
                    SettlementUsageSource.Upstream));
    }

    private sealed class RecordingFinalizationPort : IReservationFinalizationPort
    {
        internal int KnownUsageCalls { get; private set; }

        internal int ConservativeCalls { get; private set; }

        internal int TotalCalls => KnownUsageCalls + ConservativeCalls;

        internal ReservationSettlementEvidence.KnownUsage? LastKnownUsage
        {
            get;
            private set;
        }

        public ValueTask SettleKnownUsageAsync(
            DispatchedReservationHandle reservation,
            ReservationSettlementEvidence.KnownUsage usage,
            ReservationLifetimeStopReason stopReason,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ReservationStatus.Pending, reservation.Status);
            Assert.False(cancellationToken.IsCancellationRequested);
            LastKnownUsage = usage;
            KnownUsageCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask SettleConservativelyAsync(
            DispatchedReservationHandle reservation,
            ConservativeReservationSettlement settlement,
            CancellationToken cancellationToken)
        {
            Assert.Equal(ReservationStatus.Pending, reservation.Status);
            Assert.NotEqual(
                ReservationLifetimeStopReason.Completed,
                settlement.Reason);
            Assert.False(cancellationToken.IsCancellationRequested);
            ConservativeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedQuotaLedger(FakeTimeProvider timeProvider)
        : IGroupQuotaLedger
    {
        private readonly Channel<RenewReservationCommand> _renewals =
            Channel.CreateUnbounded<RenewReservationCommand>();
        private readonly TaskCompletionSource<Result<ReservationHandle>>
            _pendingRenewal = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource
            _pendingRenewalCancellationObserved = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private RenewReservationCommand? _pendingRenewalCommand;

        internal List<RenewReservationCommand> RenewCommands { get; } = [];

        internal long? FailRenewalAtSequence { get; set; }

        internal long? PendingRenewalAtSequence { get; set; }

        internal bool RegressLease { get; set; }

        internal Task PendingRenewalCancellationObserved =>
            _pendingRenewalCancellationObserved.Task;

        internal ValueTask<RenewReservationCommand> NextRenewalAsync() =>
            _renewals.Reader.ReadAsync(TestContext.Current.CancellationToken);

        public ValueTask<Result<ReservationHandle>> RenewAsync(
            RenewReservationCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewCommands.Add(command);
            if (!_renewals.Writer.TryWrite(command))
            {
                throw new InvalidOperationException(
                    "The renewal notification channel is unavailable.");
            }

            if (command.RenewalSequence == FailRenewalAtSequence)
            {
                return ValueTask.FromResult(
                    Result.Failure<ReservationHandle>(
                        "reservation_lease_lost",
                        "The scripted renewal failed."));
            }

            if (command.RenewalSequence == PendingRenewalAtSequence)
            {
                _pendingRenewalCommand = command;
                _ = cancellationToken.UnsafeRegister(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    _pendingRenewalCancellationObserved);
                return new ValueTask<Result<ReservationHandle>>(
                    _pendingRenewal.Task);
            }

            TimeSpan lease = command.Reservation.IsStreaming
                ? TimeSpan.FromSeconds(120)
                : TimeSpan.FromSeconds(300);
            DateTimeOffset leaseExpiresAt = timeProvider.GetUtcNow() + lease;
            if (leaseExpiresAt > command.Reservation.MaxExpiresAt)
            {
                leaseExpiresAt = command.Reservation.MaxExpiresAt;
            }

            if (RegressLease)
            {
                leaseExpiresAt = command.Reservation.LeaseExpiresAt.AddTicks(-1);
            }

            return ValueTask.FromResult(Result.Success(
                command.Reservation with
                {
                    LeaseExpiresAt = leaseExpiresAt,
                }));
        }

        internal void SucceedPendingRenewal(DateTimeOffset leaseExpiresAt)
        {
            RenewReservationCommand command = _pendingRenewalCommand
                ?? throw new InvalidOperationException(
                    "No scripted renewal is pending.");
            _pendingRenewal.TrySetResult(Result.Success(
                command.Reservation with
                {
                    LeaseExpiresAt = leaseExpiresAt,
                }));
        }

        public ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
            ReserveQuotaCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
            MarkReservationDispatchedCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<QuotaTransitionResult>> SettleAsync(
            SettleReservationCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
            ReleaseReservationCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private static DispatchedReservationHandle CreateReservation(bool isStreaming)
    {
        TimeSpan initialLease = isStreaming
            ? TimeSpan.FromSeconds(120)
            : TimeSpan.FromSeconds(300);
        TimeSpan maximumLifetime = isStreaming
            ? TimeSpan.FromHours(2)
            : TimeSpan.FromSeconds(600);
        ReservationHandle reservation = new(
            ReservationId: Id("01910000-0000-7000-8000-000000000001"),
            RequestId: Id("01910000-0000-7000-8000-000000000002"),
            AttemptId: Id("01910000-0000-7000-8000-000000000003"),
            AttemptIndex: 1,
            GroupId: Id("01910000-0000-7000-8000-000000000004"),
            PeriodId: Id("01910000-0000-7000-8000-000000000005"),
            AccountId: Id("01910000-0000-7000-8000-000000000006"),
            ChannelId: Id("01910000-0000-7000-8000-000000000007"),
            EstimatedTokens: 100,
            IsStreaming: isStreaming,
            LeaseOwner: "api-instance:test",
            LeaseExpiresAt: Now + initialLease,
            MaxExpiresAt: Now + maximumLifetime);
        return new DispatchedReservationHandle(
            ReservationStatus.Pending,
            reservation,
            SettlementProvider.OpenAi,
            "gpt-test",
            new TokenEstimateSplit(40, 60),
            Now);
    }

    private static EntityId Id(string value) => new(Guid.Parse(value));
}
