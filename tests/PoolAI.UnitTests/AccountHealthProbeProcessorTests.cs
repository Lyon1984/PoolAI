using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Routing.Worker;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - ADR 0011, "New unknown versus breaker half-open".
// - docs/开发执行规格-v1.0.md, AC-042 and M2-E4.
public sealed class AccountHealthProbeProcessorTests
{
    private static readonly EntityId AccountId = new(
        Guid.Parse("018f3a4b-5c6d-7e8f-9012-3456789abcde"));
    private static readonly DateTimeOffset ObservationTime = new(
        2026,
        7,
        31,
        2,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(AccountHealth.Cooling)]
    [InlineData(AccountHealth.Unknown)]
    public async Task PersistedRecoveryCandidateRequiresRedisHalfOpen(
        AccountHealth health)
    {
        AccountHealthProbeCandidate candidate = Candidate(
            health,
            lastCheckedAt: ObservationTime.AddMinutes(-1),
            retryAt: health == AccountHealth.Cooling
                ? ObservationTime.AddSeconds(-1)
                : null);
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.Closed));

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(new ScriptedJobLock());

        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.ProbeEligibleCount);
        Assert.Equal(0, result.ProbedCount);
        Assert.Equal(0, harness.Breakers.ProbeAcquireCount);
        Assert.Equal(0, harness.Leases.AcquireCount);
        Assert.Equal(0, harness.Executor.ProbeCount);
        Assert.Empty(harness.HealthWriter.Transitions);
        Assert.Empty(harness.Breakers.Records);
    }

    [Fact]
    public async Task DisabledUnknownIsControlledOnlyBeforeFirstObservation()
    {
        AccountHealthProbeCandidate initial = Candidate(
            AccountHealth.Unknown,
            lastCheckedAt: null,
            isActive: false);
        ProcessorHarness initialHarness = new(
            initial,
            Breaker(AccountBreakerState.Closed));

        AccountHealthProbeProcessResult initialResult =
            await initialHarness.ProcessAsync(new ScriptedJobLock());

        AccountBreakerRecordCommand record =
            Assert.Single(initialHarness.Breakers.Records);
        Assert.Equal(
            AccountBreakerObservationMode.ControlledActive,
            record.ObservationMode);
        Assert.Equal(1, initialResult.ProbeEligibleCount);
        Assert.Equal(1, initialResult.ProbedCount);
        Assert.Equal(1, initialHarness.Leases.AcquireCount);

        AccountHealthProbeCandidate observed = Candidate(
            AccountHealth.Unknown,
            lastCheckedAt: ObservationTime.AddMinutes(-1),
            isActive: false);
        ProcessorHarness observedHarness = new(
            observed,
            Breaker(AccountBreakerState.Closed));

        AccountHealthProbeProcessResult observedResult =
            await observedHarness.ProcessAsync(new ScriptedJobLock());

        Assert.Equal(1, observedResult.SkippedCount);
        Assert.Equal(0, observedResult.ProbeEligibleCount);
        Assert.Equal(0, observedResult.ProbedCount);
        Assert.Empty(observedHarness.Breakers.Records);
        Assert.Equal(0, observedHarness.Leases.AcquireCount);
    }

    [Fact]
    public async Task ActiveObservedUnknownUsesHalfOpenProbeAndAccountLease()
    {
        AccountHealthProbeCandidate candidate = Candidate(
            AccountHealth.Unknown,
            lastCheckedAt: ObservationTime.AddMinutes(-1));
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.HalfOpen));

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(new ScriptedJobLock());

        Assert.Equal(1, result.ProbeEligibleCount);
        Assert.Equal(1, result.ProbedCount);
        Assert.Equal(1, result.HalfOpenProbeCount);
        Assert.Equal(1, harness.Breakers.ProbeAcquireCount);
        Assert.Equal(1, harness.Breakers.Probe.CompleteCount);
        Assert.Equal(1, harness.Leases.AcquireCount);
        Assert.Equal(1, harness.Executor.ProbeCount);
        AccountHealthTransition transition =
            Assert.Single(harness.HealthWriter.Transitions);
        Assert.Equal(AccountHealth.Unknown, transition.Health);
        Assert.Empty(harness.Breakers.Records);
    }

    [Fact]
    public async Task JobLockIsRecheckedAfterProbeOwnerAcquisitionBeforeHealthWrite()
    {
        AccountHealthProbeCandidate candidate = Candidate(
            AccountHealth.Cooling,
            lastCheckedAt: ObservationTime.AddMinutes(-1),
            retryAt: ObservationTime.AddSeconds(-1));
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.HalfOpen));
        ScriptedJobLock jobLock = new(loseOnVerification: 3);

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(jobLock);

        Assert.Equal(SupplyHealthCycleStatus.Failed, result.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.LockLost, result.FailureCode);
        Assert.Equal(1, harness.Breakers.ProbeAcquireCount);
        Assert.Equal(1, harness.Breakers.Probe.DisposeCount);
        Assert.Empty(harness.HealthWriter.Transitions);
        Assert.Equal(0, harness.Leases.AcquireCount);
        Assert.Equal(0, harness.Executor.ProbeCount);
    }

    [Fact]
    public async Task JobLockIsRecheckedAfterOrdinaryAccountLeaseBeforeProbe()
    {
        AccountHealthProbeCandidate candidate = Candidate(
            AccountHealth.Healthy,
            lastCheckedAt: ObservationTime.AddMinutes(-1));
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.Closed));
        ScriptedJobLock jobLock = new(loseOnVerification: 3);

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(jobLock);

        Assert.Equal(SupplyHealthCycleStatus.Failed, result.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.LockLost, result.FailureCode);
        Assert.Equal(1, harness.Leases.AcquireCount);
        Assert.Equal(0, harness.Executor.ProbeCount);
        Assert.Empty(harness.Breakers.Records);
    }

    [Fact]
    public async Task ExpiredProbeOwnerIsAStaleCompletionNotARoundFailure()
    {
        AccountHealthProbeCandidate candidate = Candidate(
            AccountHealth.Unknown,
            lastCheckedAt: ObservationTime.AddMinutes(-1));
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.HalfOpen));
        harness.Breakers.Probe.CompletionResult =
            Result.Failure<AccountBreakerSnapshot>(
                "account_probe_not_owned",
                "The test probe generation expired.",
                retryAfterSeconds: 1);

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(new ScriptedJobLock());

        Assert.Equal(SupplyHealthCycleStatus.Succeeded, result.CycleStatus);
        Assert.Equal(SupplyHealthFailureCode.None, result.FailureCode);
        Assert.Equal(1, result.ProbedCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
        Assert.Equal(0, result.HalfOpenProbeCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, harness.Breakers.Probe.CompleteCount);
    }

    [Fact]
    public async Task HealthyClosedCandidateRetainsRoutinePassiveProbe()
    {
        AccountHealthProbeCandidate candidate = Candidate(
            AccountHealth.Healthy,
            lastCheckedAt: ObservationTime.AddMinutes(-1));
        ProcessorHarness harness = new(
            candidate,
            Breaker(AccountBreakerState.Closed));

        AccountHealthProbeProcessResult result =
            await harness.ProcessAsync(new ScriptedJobLock());

        Assert.Equal(1, result.ProbedCount);
        Assert.Equal(1, harness.Leases.AcquireCount);
        AccountBreakerRecordCommand record =
            Assert.Single(harness.Breakers.Records);
        Assert.Equal(
            AccountBreakerObservationMode.Passive,
            record.ObservationMode);
    }

    private static AccountHealthProbeCandidate Candidate(
        AccountHealth health,
        DateTimeOffset? lastCheckedAt,
        bool isActive = true,
        DateTimeOffset? retryAt = null) =>
        new(
            AccountId,
            health,
            ConcurrencyLimit: 3,
            retryAt,
            lastCheckedAt,
            AccountVersion: 7,
            CredentialRevision: 4,
            isActive);

    private static AccountBreakerSnapshot Breaker(
        AccountBreakerState state) =>
        new(
            state,
            Samples: 0,
            Failures: 0,
            ConsecutiveFailures: 0,
            OpenUntil: state == AccountBreakerState.Open
                ? ObservationTime.AddMinutes(1)
                : null,
            AccountBreakerAction.None);

    private sealed class ProcessorHarness
    {
        private readonly FakeTimeProvider _timeProvider =
            new(ObservationTime);
        private readonly ScriptedCatalog _catalog;
        private readonly RecordingOperationalEventWriter _events = new();
        private readonly SupplyHealthReadinessSummaryStore _readiness;

        internal ProcessorHarness(
            AccountHealthProbeCandidate candidate,
            AccountBreakerSnapshot breaker)
        {
            _catalog = new([candidate], []);
            Breakers = new ScriptedBreaker(breaker);
            Leases = new RecordingLeaseCoordinator();
            Executor = new RecordingProbeExecutor(
                new AccountHealthProbeResult(
                    AccountHealthProbeOutcome.Success,
                    RetryAfter: null,
                    ObservationTime,
                    UpstreamStatusCode: 200,
                    ExpectedAccountVersion: 8,
                    ExpectedCredentialRevision: 4));
            HealthWriter = new RecordingHealthWriter();
            _readiness = new SupplyHealthReadinessSummaryStore(_timeProvider);
        }

        internal ScriptedBreaker Breakers { get; }

        internal RecordingLeaseCoordinator Leases { get; }

        internal RecordingProbeExecutor Executor { get; }

        internal RecordingHealthWriter HealthWriter { get; }

        internal async Task<AccountHealthProbeProcessResult> ProcessAsync(
            IWorkerSessionLock jobLock)
        {
            using AccountHealthProbeProcessor processor = new(
                _catalog,
                Executor,
                Breakers,
                Leases,
                HealthWriter,
                _events,
                _readiness,
                _timeProvider);
            return await processor.ProcessAsync(
                jobLock,
                batchSize: 8,
                healthyProbeInterval: TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ScriptedCatalog(
        params IReadOnlyList<AccountHealthProbeCandidate>[] batches)
        : IAccountHealthProbeCatalog
    {
        private readonly Queue<IReadOnlyList<AccountHealthProbeCandidate>>
            _batches = new(batches);

        public ValueTask<Result<IReadOnlyList<AccountHealthProbeCandidate>>>
            GetDueBatchAsync(
                EntityId? afterExclusive,
                int maximumCount,
                TimeSpan healthyProbeInterval,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(8, maximumCount);
            Assert.Equal(TimeSpan.FromSeconds(30), healthyProbeInterval);
            return ValueTask.FromResult(Result.Success(
                _batches.Count == 0
                    ? (IReadOnlyList<AccountHealthProbeCandidate>)[]
                    : _batches.Dequeue()));
        }
    }

    private sealed class ScriptedBreaker(
        AccountBreakerSnapshot snapshot) : IAccountCircuitBreaker
    {
        internal RecordingProbe Probe { get; } = new(snapshot);

        internal List<AccountBreakerRecordCommand> Records { get; } = [];

        internal int ProbeAcquireCount { get; private set; }

        public ValueTask<Result<AccountBreakerSnapshot>> ReadAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AccountId, accountId);
            return ValueTask.FromResult(Result.Success(snapshot));
        }

        public ValueTask<Result<AccountBreakerSnapshot>> RecordAsync(
            AccountBreakerRecordCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(command);
            return ValueTask.FromResult(Result.Success(snapshot));
        }

        public ValueTask<Result<AccountBreakerProbeAcquireResult>>
            TryAcquireProbeAsync(
                EntityId accountId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AccountId, accountId);
            ProbeAcquireCount++;
            return ValueTask.FromResult(Result.Success(
                AccountBreakerProbeAcquireResult.Acquired(Probe)));
        }
    }

    private sealed class RecordingProbe(
        AccountBreakerSnapshot snapshot) : IAccountBreakerProbe
    {
        internal Result<AccountBreakerSnapshot> CompletionResult { get; set; } =
            Result.Success(snapshot);

        internal int CompleteCount { get; private set; }

        internal int DisposeCount { get; private set; }

        public EntityId AccountId => AccountHealthProbeProcessorTests.AccountId;

        public DateTimeOffset ExpiresAt =>
            ObservationTime.AddSeconds(10);

        public ValueTask<Result<AccountBreakerSnapshot>> CompleteAsync(
            AccountBreakerProbeCompletion completion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteCount++;
            return ValueTask.FromResult(CompletionResult);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLeaseCoordinator
        : IAccountProbeLeaseCoordinator
    {
        internal int AcquireCount { get; private set; }

        public ValueTask<Result<IAccountProbeLease>> AcquireAsync(
            AccountProbeLeaseAcquireCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AccountId, command.AccountId);
            Assert.Equal(3, command.ConcurrencyLimit);
            AcquireCount++;
            return ValueTask.FromResult(
                Result.Success<IAccountProbeLease>(
                    new RecordingAccountLease()));
        }
    }

    private sealed class RecordingAccountLease : IAccountProbeLease
    {
        public EntityId AccountId => AccountHealthProbeProcessorTests.AccountId;

        public DateTimeOffset ExpiresAt =>
            ObservationTime.AddMinutes(1);

        public ValueTask<Result<DateTimeOffset>> RenewAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(ExpiresAt));

        public ValueTask<Result<bool>> ReleaseAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingProbeExecutor(
        AccountHealthProbeResult result) : IAccountHealthProbeExecutor
    {
        internal int ProbeCount { get; private set; }

        public ValueTask<Result<AccountHealthProbeResult>> ProbeAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AccountId, accountId);
            ProbeCount++;
            return ValueTask.FromResult(Result.Success(result));
        }
    }

    private sealed class RecordingHealthWriter : IAccountHealthWriter
    {
        internal List<AccountHealthTransition> Transitions { get; } = [];

        public ValueTask<Result<AccountHealthTransitionResult>> RecordAsync(
            AccountHealthTransition transition,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transitions.Add(transition);
            AccountHealthState before = new(
                AccountHealth.Cooling,
                ObservationTime.AddSeconds(-1),
                ObservationTime.AddMinutes(-1),
                transition.ExpectedAccountVersion);
            AccountHealthState current = new(
                transition.Health,
                transition.RetryAt,
                transition.ObservedAt,
                transition.ExpectedAccountVersion + 1);
            return ValueTask.FromResult(Result.Success(
                new AccountHealthTransitionResult(
                    AccountHealthTransitionDisposition.Applied,
                    WasChanged: true,
                    before,
                    current)));
        }
    }

    private sealed class RecordingOperationalEventWriter
        : IOperationalEventWriter
    {
        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(
                "routing.account_health_probe_round_completed",
                eventName);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedJobLock(int? loseOnVerification = null)
        : IWorkerSessionLock
    {
        internal int VerifyCount { get; private set; }

        public WorkerJobIdentity Job => WorkerJobs.SupplyHealth;

        public long LockId => 1;

        public ValueTask<bool> VerifyOwnershipAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCount++;
            return ValueTask.FromResult(
                loseOnVerification is null
                || VerifyCount < loseOnVerification.Value);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
