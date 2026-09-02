using System.Numerics;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.UnitTests;

// Governing contract: ADR 0015 Account lease lifetime and M4-E1 single-attempt
// Process Manager seam.
public sealed class AccountLeaseLifetimeOperationTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        2,
        0,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task RenewsEveryTwentySecondsWithoutPreemptingFinalizationOwner()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(time);
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        Task<ReservationSettlementEvidence> execution = operation
            .ExecuteAsync(NotCancelled())
            .AsTask();
        time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
        await PumpAsync();

        Assert.Equal(1, lease.RenewalCount);
        Assert.Equal(1, operation.SuccessfulRenewals);

        upstream.CompleteKnownUsage();
        ReservationSettlementEvidence evidence = await execution;

        Assert.IsType<ReservationSettlementEvidence.KnownUsage>(evidence);
        Assert.Equal(AccountLeaseLifetimeStopReason.Completed, operation.StopReason);
        Assert.Equal(0, lease.ReleaseCount);
        Assert.Equal(0, lease.DisposeCount);
    }

    [Fact]
    public async Task ShortRemainingLeaseRenewsBeforeItsPersistedExpiry()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(
            time,
            initialRemaining: TimeSpan.FromSeconds(10));
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        Task<ReservationSettlementEvidence> execution = operation
            .ExecuteAsync(NotCancelled())
            .AsTask();
        time.Advance(TimeSpan.FromSeconds(4));
        await PumpAsync();
        Assert.Equal(0, lease.RenewalCount);

        time.Advance(TimeSpan.FromSeconds(1));
        await PumpAsync();
        Assert.Equal(1, lease.RenewalCount);

        upstream.CompleteKnownUsage();
        _ = await execution;
    }

    [Fact]
    public async Task TwoConsecutiveCoordinationFailuresCancelUpstream()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(time);
        lease.EnqueueUnavailable();
        lease.EnqueueUnavailable();
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        Task<ReservationSettlementEvidence> execution = operation
            .ExecuteAsync(NotCancelled())
            .AsTask();
        time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
        await PumpAsync();
        Assert.False(upstream.Abort.IsCompleted);

        time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
        await upstream.Abort.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        upstream.CompleteKnownUsage();
        _ = await execution;

        Assert.Equal(
            AccountLeaseLifetimeStopReason.CoordinationUnavailable,
            operation.StopReason);
        Assert.Equal(2, lease.RenewalCount);
        Assert.Equal(0, lease.ReleaseCount);
    }

    [Fact]
    public async Task SuccessfulRenewalResetsCoordinationFailureStreak()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(time);
        lease.EnqueueUnavailable();
        lease.EnqueueSuccess();
        lease.EnqueueUnavailable();
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        Task<ReservationSettlementEvidence> execution = operation
            .ExecuteAsync(NotCancelled())
            .AsTask();
        for (int index = 0; index < 3; index++)
        {
            time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
            await PumpAsync();
        }

        Assert.False(upstream.Abort.IsCompleted);
        Assert.Equal(1, operation.SuccessfulRenewals);

        upstream.CompleteKnownUsage();
        _ = await execution;
        Assert.Equal(AccountLeaseLifetimeStopReason.Completed, operation.StopReason);
    }

    [Fact]
    public async Task LostLeaseImmediatelyAbortsAndBoundsDrain()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(time);
        lease.EnqueueLost();
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        Task<ReservationSettlementEvidence> execution = operation
            .ExecuteAsync(NotCancelled())
            .AsTask();
        time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
        await upstream.Abort.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.False(execution.IsCompleted);
        time.Advance(AccountLeaseLifetimeOperation.MaximumDrainDuration);
        ReservationSettlementEvidence evidence = await execution;

        Assert.IsType<ReservationSettlementEvidence.NoKnownUsage>(evidence);
        Assert.Equal(AccountLeaseLifetimeStopReason.LeaseLost, operation.StopReason);
        Assert.True(upstream.Drain.IsCompleted);
        Assert.Equal(0, lease.ReleaseCount);
    }

    [Fact]
    public async Task ExpiredInitialLeaseDoesNotStartUpstreamOperation()
    {
        FakeTimeProvider time = new(Now);
        ScriptedAccountLease lease = new(
            time,
            initialRemaining: TimeSpan.Zero);
        ScriptedOperation upstream = new();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        ReservationSettlementEvidence evidence = await operation.ExecuteAsync(
            NotCancelled());

        ReservationSettlementEvidence.KnownUsage known = Assert.IsType<
            ReservationSettlementEvidence.KnownUsage>(evidence);
        Assert.Equal(SettlementUsageSource.ConfirmedNoExecution, known.UsageSource);
        Assert.Equal(BigInteger.Zero, known.Usage.TotalTokens);
        Assert.Equal(AccountLeaseLifetimeStopReason.LeaseLost, operation.StopReason);
        Assert.Equal(0, upstream.ExecutionCount);
        Assert.Equal(0, lease.RenewalCount);
    }

    [Fact]
    public async Task RenewalMonitorStartsBeforeUpstreamOperation()
    {
        List<string> events = new();
        RecordingTimeProvider time = new(Now, events);
        ScriptedAccountLease lease = new(time);
        ScriptedOperation upstream = new(events);
        upstream.CompleteKnownUsage();
        AccountLeaseLifetimeOperation operation = Create(
            lease,
            upstream,
            time);

        _ = await operation.ExecuteAsync(NotCancelled());

        Assert.Equal(["monitor", "operation"], events);
    }

    private static AccountLeaseLifetimeOperation Create(
        IAccountLease lease,
        IReservationLifetimeOperation operation,
        TimeProvider time) => new(
            lease,
            operation,
            time,
            AccountLeaseLifetimeOperation.MaximumDrainDuration);

    private static ReservationLifetimeCancellation NotCancelled() => new(
        CancellationToken.None,
        CancellationToken.None);

    private static async Task PumpAsync()
    {
        for (int index = 0; index < 8; index++)
        {
            await Task.Yield();
        }
    }

    private sealed class ScriptedOperation : IReservationLifetimeOperation
    {
        private readonly TaskCompletionSource<ReservationSettlementEvidence>
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _abort =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drain =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string>? _events;

        internal ScriptedOperation(List<string>? events = null)
        {
            _events = events;
        }

        internal Task Abort => _abort.Task;

        internal Task Drain => _drain.Task;

        internal int ExecutionCount { get; private set; }

        public ValueTask<ReservationSettlementEvidence> ExecuteAsync(
            ReservationLifetimeCancellation cancellation)
        {
            _events?.Add("operation");
            ExecutionCount++;
            _ = cancellation.AbortUpstream.UnsafeRegister(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _abort);
            _ = cancellation.Drain.UnsafeRegister(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _drain);
            return new ValueTask<ReservationSettlementEvidence>(_completion.Task);
        }

        internal void CompleteKnownUsage() => _completion.TrySetResult(
            new ReservationSettlementEvidence.KnownUsage(
                new TokenUsage(10, 5, 0, 0, 0),
                SettlementUsageSource.Upstream));
    }

    private sealed class ScriptedAccountLease : IAccountLease
    {
        private readonly Queue<AccountLeaseRenewResult> _results = new();
        private readonly TimeProvider _time;

        internal ScriptedAccountLease(
            TimeProvider time,
            TimeSpan? initialRemaining = null)
        {
            _time = time;
            Route = CreateRoute(
                time.GetUtcNow()
                + (initialRemaining ?? TimeSpan.FromSeconds(60)));
        }

        public AccountRoute Route { get; private set; }

        internal int RenewalCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public ValueTask<AccountLeaseRenewResult> RenewAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewalCount++;
            AccountLeaseRenewResult result = _results.Count == 0
                ? SuccessResult()
                : _results.Dequeue();
            if (result.Disposition == AccountLeaseRenewDisposition.Renewed
                && result.Route is not null)
            {
                Route = result.Route;
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<Result<bool>> ReleaseAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCount++;
            return ValueTask.FromResult(Result.Success(true));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        internal void EnqueueLost() => _results.Enqueue(
            AccountLeaseRenewResult.Lost);

        internal void EnqueueUnavailable() => _results.Enqueue(
            AccountLeaseRenewResult.Unavailable);

        internal void EnqueueSuccess() => _results.Enqueue(
            AccountLeaseRenewResult.Renewed(
            Route with
            {
                LeaseExpiresAt = _time.GetUtcNow().AddMinutes(5),
            }));

        private AccountLeaseRenewResult SuccessResult() =>
            AccountLeaseRenewResult.Renewed(
            Route with
            {
                LeaseExpiresAt = _time.GetUtcNow().AddSeconds(60),
            });

        private static AccountRoute CreateRoute(DateTimeOffset leaseExpiresAt) =>
            new(
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                AccountRouteProvider.OpenAi,
                "gpt-5",
                "gpt-5",
                new Uri("https://api.openai.com/v1", UriKind.Absolute),
                new AccountRouteCapabilities(
                    Responses: true,
                    ChatCompletions: true,
                    FunctionTools: true,
                    Streaming: true),
                leaseExpiresAt,
                SupplyConfigurationVersion: 2,
                ChannelVersion: 3,
                AccountVersion: 4,
                CredentialRevision: 5);
    }

    private sealed class RecordingTimeProvider(
        DateTimeOffset now,
        List<string> events) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            events.Add("monitor");
            return TimeProvider.System.CreateTimer(
                callback,
                state,
                dueTime,
                period);
        }
    }
}
