using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Application;
using PoolAI.Modules.Routing.Infrastructure;
using PoolAI.Modules.Routing.Infrastructure.Workers;
using PoolAI.Modules.Routing.Worker;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/runtime/redis-contract.md, Account lease ownership and TTL rules.
// - docs/开发执行规格-v1.0.md, AC-042 and the Supply health Worker.
public sealed class AccountProbeLeaseAndWorkerContractTests
{
    private static readonly EntityId AccountId = new(
        Guid.Parse("018f3a4b-5c6d-7e8f-9012-3456789abcde"));
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        31,
        3,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task LeaseCoordinatorRejectsUnsupportedConcurrency(
        int concurrencyLimit)
    {
        ScriptedLeaseSet leases = new();
        AccountProbeLeaseCoordinator coordinator = new(leases);

        Result<IAccountProbeLease> result = await coordinator.AcquireAsync(
            new AccountProbeLeaseAcquireCommand(AccountId, concurrencyLimit),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_request", result.Error.Code);
        Assert.Equal(0, leases.AcquireCount);
    }

    [Fact]
    public async Task LeaseCoordinatorMapsCoordinationUnavailability()
    {
        ScriptedLeaseSet leases = new()
        {
            AcquireResult = CoordinationLeaseAcquireResult.Unavailable,
        };
        AccountProbeLeaseCoordinator coordinator = new(leases);

        Result<IAccountProbeLease> result = await coordinator.AcquireAsync(
            new AccountProbeLeaseAcquireCommand(AccountId, 3),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
    }

    [Fact]
    public async Task LeaseCoordinatorRoundsCapacityRetryUp()
    {
        ScriptedLeaseSet leases = new()
        {
            AcquireResult = CoordinationLeaseAcquireResult.CapacityExceeded(
                activeCount: 3,
                retryAfter: TimeSpan.FromMilliseconds(1_001)),
        };
        AccountProbeLeaseCoordinator coordinator = new(leases);

        Result<IAccountProbeLease> result = await coordinator.AcquireAsync(
            new AccountProbeLeaseAcquireCommand(AccountId, 3),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("account_capacity_unavailable", result.Error.Code);
        Assert.Equal(2, result.Error.RetryAfterSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseCoordinatorReturnsOwnedLeaseForAcquireOrRenew(
        bool renewed)
    {
        DateTimeOffset expiresAt = Now.AddSeconds(31);
        ScriptedLeaseSet leases = new()
        {
            AcquireResult = CoordinationLeaseAcquireResult.Acquired(
                activeCount: 1,
                expiresAt,
                renewed),
            ReleaseResult = CoordinationLeaseReleaseResult.Released,
        };
        AccountProbeLeaseCoordinator coordinator = new(leases);

        Result<IAccountProbeLease> result = await coordinator.AcquireAsync(
            new AccountProbeLeaseAcquireCommand(AccountId, 3),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(AccountId, result.Value.AccountId);
        Assert.Equal(expiresAt, result.Value.ExpiresAt);
        Assert.Equal(3, leases.LastAcquireRequest?.Limit);
        Assert.Equal(
            AccountRouter.LeaseKey(AccountId),
            leases.LastAcquireRequest?.KeyBase);
        Assert.Equal(32, leases.LastAcquireRequest?.Owner.Length);

        await result.Value.DisposeAsync();

        Assert.Equal(1, leases.ReleaseCount);
    }

    [Fact]
    public async Task LeaseRenewUpdatesExpiryAndLostLeaseStaysLost()
    {
        DateTimeOffset originalExpiry = Now.AddSeconds(30);
        DateTimeOffset renewedExpiry = Now.AddMinutes(1);
        ScriptedLeaseSet leases = new()
        {
            RenewResults = new Queue<CoordinationLeaseRenewResult>(
            [
                CoordinationLeaseRenewResult.Renewed(renewedExpiry),
                CoordinationLeaseRenewResult.Lost,
            ]),
        };
        AccountProbeLease lease = new(
            leases,
            AccountId,
            "0123456789abcdef0123456789abcdef",
            originalExpiry);

        Result<DateTimeOffset> renewed = await lease.RenewAsync(
            TestContext.Current.CancellationToken);
        Result<DateTimeOffset> lost = await lease.RenewAsync(
            TestContext.Current.CancellationToken);
        Result<DateTimeOffset> stillLost = await lease.RenewAsync(
            TestContext.Current.CancellationToken);

        Assert.True(renewed.IsSuccess);
        Assert.Equal(renewedExpiry, renewed.Value);
        Assert.Equal(renewedExpiry, lease.ExpiresAt);
        Assert.Equal("account_capacity_unavailable", lost.Error.Code);
        Assert.Equal("account_capacity_unavailable", stillLost.Error.Code);
        Assert.Equal(2, leases.RenewCount);
    }

    [Fact]
    public async Task LeaseCanRetryAfterUnavailableRenewAndRelease()
    {
        DateTimeOffset renewedExpiry = Now.AddMinutes(1);
        ScriptedLeaseSet leases = new()
        {
            RenewResults = new Queue<CoordinationLeaseRenewResult>(
            [
                CoordinationLeaseRenewResult.Unavailable,
                CoordinationLeaseRenewResult.Renewed(renewedExpiry),
            ]),
            ReleaseResults = new Queue<CoordinationLeaseReleaseResult>(
            [
                CoordinationLeaseReleaseResult.Unavailable,
                CoordinationLeaseReleaseResult.NotOwned,
            ]),
        };
        AccountProbeLease lease = new(
            leases,
            AccountId,
            "0123456789abcdef0123456789abcdef",
            Now.AddSeconds(30));

        Result<DateTimeOffset> unavailableRenew = await lease.RenewAsync(
            TestContext.Current.CancellationToken);
        Result<DateTimeOffset> renewed = await lease.RenewAsync(
            TestContext.Current.CancellationToken);
        Result<bool> unavailableRelease = await lease.ReleaseAsync(
            TestContext.Current.CancellationToken);
        Result<bool> notOwned = await lease.ReleaseAsync(
            TestContext.Current.CancellationToken);
        Result<bool> alreadyReleased = await lease.ReleaseAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("coordination_unavailable", unavailableRenew.Error.Code);
        Assert.True(renewed.IsSuccess);
        Assert.Equal(renewedExpiry, renewed.Value);
        Assert.Equal("coordination_unavailable", unavailableRelease.Error.Code);
        Assert.True(notOwned.IsSuccess);
        Assert.False(notOwned.Value);
        Assert.True(alreadyReleased.IsSuccess);
        Assert.False(alreadyReleased.Value);
        Assert.Equal(2, leases.ReleaseCount);
    }

    [Fact]
    public async Task LeaseDisposeTreatsDisposedCoordinationClientAsTtlCleanup()
    {
        ScriptedLeaseSet leases = new()
        {
            ThrowDisposedOnRelease = true,
        };
        AccountProbeLease lease = new(
            leases,
            AccountId,
            "0123456789abcdef0123456789abcdef",
            Now.AddSeconds(30));

        await lease.DisposeAsync();

        Assert.Equal(1, leases.ReleaseCount);
    }

    [Fact]
    public async Task WorkerPublishesStandbyWhenJobLockIsAlreadyOwned()
    {
        RecordingReadinessStore readiness = new();
        using AccountHealthProbeProcessor processor = CreateProcessor(readiness);
        using AccountHealthWorkerService service = new(
            new ScriptedLockProvider(null),
            processor,
            new AccountHealthWorkerOptions(TimeSpan.FromMilliseconds(10), 8),
            readiness,
            new FakeTimeProvider(Now),
            NullLogger<AccountHealthWorkerService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        SupplyHealthReadinessSummary summary = await readiness.FirstUpdate.Task
            .WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SupplyHealthCycleStatus.Standby, summary.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.NotOwner, summary.FailureCode);
        Assert.Equal(Now, summary.ObservedAt);
    }

    [Fact]
    public async Task WorkerProcessesAndDisposesAnOwnedJobLock()
    {
        RecordingReadinessStore readiness = new();
        RecordingJobLock jobLock = new();
        using AccountHealthProbeProcessor processor = CreateProcessor(readiness);
        using AccountHealthWorkerService service = new(
            new ScriptedLockProvider(jobLock),
            processor,
            new AccountHealthWorkerOptions(TimeSpan.FromMilliseconds(10), 8),
            readiness,
            new FakeTimeProvider(Now),
            NullLogger<AccountHealthWorkerService>.Instance);

        await service.RunSingleRoundAsync(TestContext.Current.CancellationToken);
        SupplyHealthReadinessSummary summary =
            await readiness.FirstUpdate.Task;
        await jobLock.Disposed.Task.WaitAsync(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(SupplyHealthCycleStatus.Succeeded, summary.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.None, summary.FailureCode);
        Assert.Equal(1, jobLock.VerifyCount);
    }

    [Theory]
    [InlineData("database", "DependencyUnavailable")]
    [InlineData("http", "UpstreamProbeFailed")]
    [InlineData("io", "UpstreamProbeFailed")]
    [InlineData("timeout", "UpstreamProbeFailed")]
    [InlineData("contract", "ContractFailure")]
    [InlineData("unexpected", "UnexpectedFailure")]
    public async Task WorkerMapsRoundFailuresToStableReadinessCodes(
        string exceptionKind,
        string expectedFailure)
    {
        RecordingReadinessStore readiness = new();
        using AccountHealthProbeProcessor processor = CreateProcessor(readiness);
        using AccountHealthWorkerService service = new(
            new ThrowingLockProvider(CreateException(exceptionKind)),
            processor,
            new AccountHealthWorkerOptions(TimeSpan.FromMilliseconds(10), 8),
            readiness,
            new FakeTimeProvider(Now),
            NullLogger<AccountHealthWorkerService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        SupplyHealthReadinessSummary summary = await readiness.FirstUpdate.Task
            .WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SupplyHealthCycleStatus.Failed, summary.CycleStatus);
        Assert.Equal(expectedFailure, summary.FailureCode.ToString());
        Assert.Equal(Now, summary.ObservedAt);
    }

    private static Exception CreateException(string kind) =>
        kind switch
        {
            "database" => new TestDbException(),
            "http" => new HttpRequestException("test"),
            "io" => new IOException("test"),
            "timeout" => new TimeoutException("test"),
            "contract" => new InvalidOperationException("test"),
            _ => new NotSupportedException("test"),
        };

    private static AccountHealthProbeProcessor CreateProcessor(
        ISupplyHealthReadinessSummaryStore readiness) =>
        new(
            new EmptyCatalog(),
            new UnusedProbeExecutor(),
            new UnusedCircuitBreaker(),
            new UnusedLeaseCoordinator(),
            new UnusedHealthWriter(),
            new NoOpOperationalEventWriter(),
            readiness,
            new FakeTimeProvider(Now));

    private sealed class ScriptedLeaseSet : ICoordinationLeaseSet
    {
        internal CoordinationLeaseAcquireResult AcquireResult { get; set; } =
            CoordinationLeaseAcquireResult.Unavailable;

        internal Queue<CoordinationLeaseRenewResult> RenewResults { get; set; } =
            new([CoordinationLeaseRenewResult.Unavailable]);

        internal Queue<CoordinationLeaseReleaseResult> ReleaseResults
        {
            get;
            set;
        } = new([CoordinationLeaseReleaseResult.Unavailable]);

        internal CoordinationLeaseReleaseResult? ReleaseResult
        {
            set => ReleaseResults = new Queue<CoordinationLeaseReleaseResult>(
                [value.GetValueOrDefault()]);
        }

        internal bool ThrowDisposedOnRelease { get; set; }

        internal int AcquireCount { get; private set; }

        internal int RenewCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        internal CoordinationLeaseAcquireRequest? LastAcquireRequest
        {
            get;
            private set;
        }

        public ValueTask<CoordinationLeaseAcquireResult> AcquireAsync(
            CoordinationLeaseAcquireRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AcquireCount++;
            LastAcquireRequest = request;
            return ValueTask.FromResult(AcquireResult);
        }

        public ValueTask<CoordinationLeaseRenewResult> RenewAsync(
            CoordinationLeaseOwner request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewCount++;
            return ValueTask.FromResult(RenewResults.Dequeue());
        }

        public ValueTask<CoordinationLeaseReleaseResult> ReleaseAsync(
            CoordinationLeaseOwner request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCount++;
            ObjectDisposedException.ThrowIf(ThrowDisposedOnRelease, this);

            return ValueTask.FromResult(ReleaseResults.Dequeue());
        }
    }

    private sealed class RecordingReadinessStore
        : ISupplyHealthReadinessSummaryStore
    {
        private SupplyHealthReadinessSummary _current =
            SupplyHealthReadinessSummaryStore.Empty(
                Now,
                SupplyHealthCycleStatus.Standby,
                SupplyHealthFailureCode.NotOwner);

        internal TaskCompletionSource<SupplyHealthReadinessSummary> FirstUpdate
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SupplyHealthReadinessSummary Current => _current;

        public void Update(SupplyHealthReadinessSummary summary)
        {
            _current = summary;
            FirstUpdate.TrySetResult(summary);
        }
    }

    private sealed class ScriptedLockProvider(IWorkerSessionLock? first)
        : IWorkerSessionLockProvider
    {
        private IWorkerSessionLock? _next = first;
        private int _calls;

        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(WorkerJobs.SupplyHealth, job);
            return ValueTask.FromResult(
                Interlocked.Increment(ref _calls) == 1
                    ? Interlocked.Exchange(ref _next, null)
                    : null);
        }
    }

    private sealed class ThrowingLockProvider(Exception exception)
        : IWorkerSessionLockProvider
    {
        public ValueTask<IWorkerSessionLock?> TryAcquireAsync(
            WorkerJobIdentity job,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }

    private sealed class RecordingJobLock : IWorkerSessionLock
    {
        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int VerifyCount { get; private set; }

        public WorkerJobIdentity Job => WorkerJobs.SupplyHealth;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCount++;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyCatalog : IAccountHealthProbeCatalog
    {
        public ValueTask<Result<IReadOnlyList<AccountHealthProbeCandidate>>>
            GetDueBatchAsync(
                EntityId? afterExclusive,
                int maximumCount,
                TimeSpan healthyProbeInterval,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Success<
                IReadOnlyList<AccountHealthProbeCandidate>>([]));
        }
    }

    private sealed class UnusedProbeExecutor : IAccountHealthProbeExecutor
    {
        public ValueTask<Result<AccountHealthProbeResult>> ProbeAsync(
            EntityId accountId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not probe.");
    }

    private sealed class UnusedCircuitBreaker : IAccountCircuitBreaker
    {
        public ValueTask<Result<AccountBreakerSnapshot>> ReadAsync(
            EntityId accountId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not read.");

        public ValueTask<Result<AccountBreakerSnapshot>> RecordAsync(
            AccountBreakerRecordCommand command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not record.");

        public ValueTask<Result<AccountBreakerProbeAcquireResult>>
            TryAcquireProbeAsync(
                EntityId accountId,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not acquire.");
    }

    private sealed class UnusedLeaseCoordinator : IAccountProbeLeaseCoordinator
    {
        public ValueTask<Result<IAccountProbeLease>> AcquireAsync(
            AccountProbeLeaseAcquireCommand command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not lease.");
    }

    private sealed class UnusedHealthWriter : IAccountHealthWriter
    {
        public ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
            AccountHealthTransition transition,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The empty catalog must not write.");
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestDbException : DbException;
}
