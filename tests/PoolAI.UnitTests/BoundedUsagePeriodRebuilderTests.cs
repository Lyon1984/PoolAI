using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Usage.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;
using PoolAI.Modules.Usage.Worker;

namespace PoolAI.UnitTests;

public sealed class BoundedUsagePeriodRebuilderTests
{
    private static readonly EntityId GroupId = Id("81000000-0000-0000-0000-000000000001");
    private static readonly EntityId PeriodId = Id("82000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FirstHour = new(
        2030,
        1,
        2,
        3,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task RebuildAcceptsOnlyTheDedicatedUsageRebuildJobLock()
    {
        Scenario scenario = new(expectedConsumedTokens: 0);
        RecordingJobLock wrongLock = new(
            WorkerJobs.QuotaReconciliation,
            unitOfWorkFactory: null,
            ownership: [true],
            fencedUnits: [true]);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            scenario.Rebuilder.RebuildAsync(
                wrongLock,
                Request(FirstHour, FirstHour),
                TestToken).AsTask());

        Assert.Equal("jobLock", exception.ParamName);
        Assert.Empty(scenario.Operations);
        Assert.Equal(0, wrongLock.VerifyCalls);
    }

    [Fact]
    public async Task RebuildRangeAllowsExactlySevenHundredFortyFourUtcHours()
    {
        Scenario scenario = new(expectedConsumedTokens: 0, ownership: [false, false]);
        DateTimeOffset allowedLast = FirstHour.AddHours(
            UsagePeriodProjectionRebuilder.MaximumBucketCount - 1);

        BoundedUsagePeriodRebuildResult allowed = await scenario.Rebuilder.RebuildAsync(
            scenario.JobLock,
            Request(FirstHour, allowedLast),
            TestToken);

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.OwnershipLost, allowed.Disposition);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            scenario.Rebuilder.RebuildAsync(
                scenario.JobLock,
                Request(FirstHour, allowedLast.AddHours(1)),
                TestToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scenario.Rebuilder.RebuildAsync(
                scenario.JobLock,
                Request(FirstHour.AddMinutes(1), allowedLast),
                TestToken).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            scenario.Rebuilder.RebuildAsync(
                scenario.JobLock,
                Request(
                    new DateTimeOffset(
                        2030,
                        1,
                        2,
                        3,
                        0,
                        0,
                        TimeSpan.FromHours(1)),
                    allowedLast),
                TestToken).AsTask());
    }

    [Fact]
    public async Task LastRepresentableUtcHourDoesNotOverflowBucketEnumeration()
    {
        DateTimeOffset lastRepresentableHour = new(
            9999,
            12,
            31,
            23,
            0,
            0,
            TimeSpan.Zero);
        Scenario scenario = new(expectedConsumedTokens: 0);
        scenario.HourReader.Set(lastRepresentableHour, []);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(lastRepresentableHour, lastRepresentableHour));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.RebuiltBucketCount);
    }

    [Fact]
    public async Task OwnershipLossBeforeClaimDoesNotOpenAUnitOfWork()
    {
        Scenario scenario = new(expectedConsumedTokens: 0, ownership: [false]);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.OwnershipLost, result.Disposition);
        Assert.Equal(0, scenario.Factory.BeginCalls);
        Assert.Equal(0, scenario.Checkpoint.ClaimCalls);
        Assert.Empty(scenario.Writer.Calls);
    }

    [Fact]
    public async Task BusyCheckpointStopsBeforeAuthoritativeOrBucketReads()
    {
        Scenario scenario = new(expectedConsumedTokens: 0);
        scenario.Checkpoint.ClaimResult = UsageAggregationClaimResult.Busy;

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Busy, result.Disposition);
        Assert.Equal(1, scenario.Checkpoint.ClaimCalls);
        Assert.Equal(0, scenario.FactReader.ReadCalls);
        Assert.Empty(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(0, scenario.Checkpoint.ReleaseCalls);
    }

    [Fact]
    public async Task OwnershipLossAfterClaimReleasesTheFrozenCheckpoint()
    {
        Scenario scenario = new(expectedConsumedTokens: 0, ownership: [true, false]);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.OwnershipLost, result.Disposition);
        Assert.Equal(1, scenario.FactReader.ReadCalls);
        Assert.Empty(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task CheckpointLeaseLossStopsBeforeReadingOrWritingTheHour()
    {
        Scenario scenario = new(expectedConsumedTokens: 0, heartbeats: [false]);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
            result.Disposition);
        Assert.Equal(1, scenario.Checkpoint.HeartbeatCalls);
        Assert.Single(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task LostHeartbeatIsNotMaskedWhenTheStaleLeaseCannotBeReleased()
    {
        Scenario scenario = new(expectedConsumedTokens: 0, heartbeats: [false]);
        scenario.Checkpoint.ReleaseResult = false;

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
            result.Disposition);
        Assert.Equal(0, result.RebuiltBucketCount);
        Assert.Single(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(17, scenario.Checkpoint.LastEventSequence);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Theory]
    [InlineData("counter")]
    [InlineData("reserved")]
    [InlineData("event_chain")]
    [InlineData("coverage")]
    [InlineData("latest")]
    [InlineData("foreign_checkpoint")]
    public async Task UnhealthyAuthoritativeStateIsRejectedWithoutProjectionMutation(
        string corruption)
    {
        Scenario scenario = new(expectedConsumedTokens: 15);
        scenario.FactReader.Snapshot = Corrupt(
            scenario.FactReader.Snapshot!,
            corruption);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.InvalidAuthoritativeState,
            result.Disposition);
        Assert.Empty(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task ClaimFreezesCheckpointBeforeEachFactReadAndEveryBucketUsesShortUnits()
    {
        Scenario scenario = new(expectedConsumedTokens: 20);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);
        scenario.HourReader.Set(
            FirstHour.AddHours(1),
            [Fact(FirstHour.AddHours(1), input: 3, output: 2)]);
        scenario.Writer.Seed(Projection(FirstHour, input: 999, output: 0));

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour.AddHours(1)));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Completed, result.Disposition);
        Assert.Equal(2, result.RebuiltBucketCount);
        int claim = scenario.Operations.IndexOf("checkpoint:claim:17");
        int authoritative = scenario.Operations.IndexOf("authoritative:17");
        int firstHourRead = scenario.Operations.IndexOf("hour:2030-01-02T03:00:00.0000000+00:00:17");
        int firstHourWrite = scenario.Operations.IndexOf("write:2030-01-02T03:00:00.0000000+00:00:value");
        Assert.True(claim >= 0);
        Assert.True(claim < authoritative);
        Assert.True(authoritative < firstHourRead);
        Assert.True(firstHourRead < firstHourWrite);
        Assert.Equal(scenario.Factory.BeginCalls, scenario.Factory.CommitCalls);
        Assert.Equal(scenario.Factory.BeginCalls, scenario.Factory.DisposeCalls);
        Assert.Equal(scenario.Factory.BeginCalls, scenario.Factory.Contexts.Count);
        Assert.Equal(
            scenario.Factory.Contexts.Count,
            scenario.Factory.Contexts.Distinct().Count());
        Assert.Equal(1, scenario.Factory.MaximumActiveCount);
        Assert.Equal(2, scenario.HourReader.Calls.Count);
        Assert.Equal(2, scenario.Writer.Calls.Count);
        Assert.Equal(3, scenario.Checkpoint.HeartbeatContexts.Count);
        Assert.All(
            scenario.Writer.Calls,
            write => Assert.Contains(
                write.Context,
                scenario.Checkpoint.HeartbeatContexts));
        Assert.All(
            scenario.HourReader.Calls,
            read => Assert.DoesNotContain(
                scenario.Writer.Calls,
                write => ReferenceEquals(read.Context, write.Context)));
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
        Assert.Equal(17, scenario.Checkpoint.LastEventSequence);
    }

    [Fact]
    public async Task JobFenceLossAfterFactReadCannotReachTheProjectionWriter()
    {
        Scenario scenario = new(
            expectedConsumedTokens: 15,
            fencedUnits: [false]);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.OwnershipLost,
            result.Disposition);
        Assert.Single(scenario.HourReader.Calls);
        Assert.Empty(scenario.Writer.Calls);
        Assert.Equal(0, scenario.Checkpoint.HeartbeatCalls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
    }

    [Fact]
    public async Task EmptyHourDeletesTheStaleDerivedBucket()
    {
        Scenario scenario = new(expectedConsumedTokens: 0);
        scenario.HourReader.Set(FirstHour, []);
        scenario.Writer.Seed(Projection(FirstHour, input: 4, output: 1));

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Completed, result.Disposition);
        ProjectionWrite call = Assert.Single(scenario.Writer.Calls);
        Assert.Equal(FirstHour, call.BucketStart);
        Assert.Null(call.Projection);
        Assert.Empty(scenario.Writer.Committed);
        Assert.Equal(17, result.CheckpointSourceEventSequence);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task PartialRangePreservesRemainingMismatchAtTheSameCheckpoint()
    {
        Scenario scenario = new(expectedConsumedTokens: 20);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);
        scenario.Writer.Seed(Projection(FirstHour, input: 1, output: 0));
        scenario.Writer.Seed(Projection(FirstHour.AddHours(1), input: 4, output: 0));

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.StillMismatched,
            result.Disposition);
        Assert.Equal(BigInteger.One, result.RemainingProjectionVariance);
        Assert.Equal(1, result.RebuiltBucketCount);
        Assert.Equal(new BigInteger(15), scenario.Writer.Committed[FirstHour].Group.TotalTokens);
        Assert.Equal(
            new BigInteger(4),
            scenario.Writer.Committed[FirstHour.AddHours(1)].Group.TotalTokens);
        Assert.Equal(17, scenario.Checkpoint.LastEventSequence);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task FullRangeRebuildCompletesWithoutAdvancingCheckpoint()
    {
        Scenario scenario = new(expectedConsumedTokens: 20);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);
        scenario.HourReader.Set(
            FirstHour.AddHours(1),
            [Fact(FirstHour.AddHours(1), input: 3, output: 2)]);
        scenario.Writer.Seed(Projection(FirstHour, input: 1, output: 0));
        scenario.Writer.Seed(Projection(FirstHour.AddHours(1), input: 4, output: 0));

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour.AddHours(1)));

        Assert.Equal(BoundedUsagePeriodRebuildDisposition.Completed, result.Disposition);
        Assert.Equal(BigInteger.Zero, result.RemainingProjectionVariance);
        Assert.Equal(2, result.RebuiltBucketCount);
        Assert.Equal(
            new BigInteger(20),
            scenario.Writer.Committed.Values.Aggregate(
                BigInteger.Zero,
                static (total, projection) => total + projection.Group.TotalTokens));
        Assert.Equal(17, result.CheckpointSourceEventSequence);
        Assert.Equal(17, scenario.Checkpoint.LastEventSequence);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
    }

    [Fact]
    public async Task FinalHeartbeatLossCannotReportACompletedRecovery()
    {
        Scenario scenario = new(
            expectedConsumedTokens: 15,
            heartbeats: [true, false]);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
            result.Disposition);
        Assert.Equal(1, result.RebuiltBucketCount);
        Assert.Equal(2, scenario.Checkpoint.HeartbeatCalls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    [Fact]
    public async Task ConcurrentClaimBeforeReleaseCannotReportACompletedRecovery()
    {
        Scenario scenario = new(expectedConsumedTokens: 15);
        scenario.HourReader.Set(FirstHour, [Fact(FirstHour, input: 10, output: 5)]);
        scenario.Checkpoint.ReleaseResult = false;

        BoundedUsagePeriodRebuildResult result = await scenario.RebuildAsync(
            Request(FirstHour, FirstHour));

        Assert.Equal(
            BoundedUsagePeriodRebuildDisposition.CheckpointLeaseLost,
            result.Disposition);
        Assert.Equal(1, result.RebuiltBucketCount);
        Assert.Equal(2, scenario.Checkpoint.HeartbeatCalls);
        Assert.Equal(1, scenario.Checkpoint.ReleaseCalls);
        Assert.Equal(0, scenario.Checkpoint.AdvanceCalls);
    }

    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private static BoundedUsagePeriodRebuildRequest Request(
        DateTimeOffset first,
        DateTimeOffset last) => new(GroupId, PeriodId, first, last);

    private static AttemptSettlementFact Fact(
        DateTimeOffset bucketStart,
        long input,
        long output) => new(
            EntityId.New(),
            EntityId.New(),
            AttemptIndex: 0,
            EntityId.New(),
            GroupId,
            PeriodId,
            EntityId.New(),
            EntityId.New(),
            SettlementProvider.OpenAi,
            RequestedModel: "requested-model",
            UpstreamModel: "upstream-model",
            UsageAttemptOutcome.Succeeded,
            UpstreamHttpStatus: 200,
            ErrorCode: null,
            IsStreaming: false,
            new AttemptUsage(
                new TokenUsage(input, output, 0, 0, 0),
                SettlementUsageSource.Upstream,
                IsEstimated: false),
            Adjustment: null,
            bucketStart.AddMinutes(1),
            FirstTokenAt: null,
            bucketStart.AddMinutes(2));

    private static UsageHourProjection Projection(
        DateTimeOffset bucketStart,
        long input,
        long output) => new(
            GroupId,
            PeriodId,
            bucketStart,
            new UsageHourlyAggregate(
                RequestCount: 1,
                AttemptCount: 1,
                FailureCount: 0,
                FailoverCount: 0,
                EstimatedAttemptCount: 0,
                new BigInteger(input),
                new BigInteger(output),
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            []);

    private static GroupQuotaReconciliationFactSnapshot Authoritative(
        BigInteger expectedConsumedTokens) => new(
            GroupId,
            PeriodId,
            CheckpointSourceEventSequence: 17,
            LedgerTotalTokens: 1000,
            LedgerConsumedTokens: expectedConsumedTokens,
            LedgerReservedTokens: BigInteger.Zero,
            FactConsumedTokens: expectedConsumedTokens,
            PendingReservationTokens: BigInteger.Zero,
            PendingReservationCount: 0,
            OverdueReservationCount: 0,
            OldestOverdueAt: null,
            ExpectedConsumedAtCheckpoint: expectedConsumedTokens,
            CheckpointBelongsToGroup: true,
            LatestPeriodEventSequence: 17,
            LatestPeriodEventOccurredAt: FirstHour,
            EventChainConsistent: true,
            FactEventCoverageConsistent: true,
            LatestEventMatchesLedger: true,
            OverageTokens: BigInteger.Zero,
            CheckedAt: FirstHour.AddHours(1),
            IsCurrentPeriod: true,
            FirstPeriodEventSequence: 1,
            LatestGroupEventSequence: 17,
            PeriodEventCount: 17);

    private static GroupQuotaReconciliationFactSnapshot Corrupt(
        GroupQuotaReconciliationFactSnapshot snapshot,
        string corruption) => corruption switch
        {
            "counter" => snapshot with
            {
                LedgerConsumedTokens = snapshot.LedgerConsumedTokens + 1,
            },
            "reserved" => snapshot with { LedgerReservedTokens = BigInteger.One },
            "event_chain" => snapshot with { EventChainConsistent = false },
            "coverage" => snapshot with { FactEventCoverageConsistent = false },
            "latest" => snapshot with { LatestEventMatchesLedger = false },
            "foreign_checkpoint" => snapshot with { CheckpointBelongsToGroup = false },
            _ => throw new InvalidOperationException("Unknown authoritative corruption."),
        };

    private sealed class Scenario
    {
        internal Scenario(
            BigInteger expectedConsumedTokens,
            IEnumerable<bool>? ownership = null,
            IEnumerable<bool>? heartbeats = null,
            IEnumerable<bool>? fencedUnits = null)
        {
            Operations = [];
            Factory = new RecordingUnitOfWorkFactory(Operations);
            Writer = new RecordingProjectionWriter(Operations);
            ProjectionReader = new RecordingProjectionReader(Writer, Operations);
            FactReader = new RecordingFactReader(
                Authoritative(expectedConsumedTokens),
                Operations);
            HourReader = new RecordingHourReader(Operations);
            Checkpoint = new RecordingCheckpoint(17, heartbeats, Operations);
            JobLock = new RecordingJobLock(
                WorkerJobs.UsageRebuild,
                Factory,
                ownership?.ToArray() ?? [true, true, true, true],
                fencedUnits?.ToArray() ?? [true, true, true, true]);
            Rebuilder = new UsagePeriodProjectionRebuilder(
                Factory,
                FactReader,
                HourReader,
                ProjectionReader,
                Writer,
                Checkpoint);
        }

        internal List<string> Operations { get; }

        internal RecordingUnitOfWorkFactory Factory { get; }

        internal RecordingFactReader FactReader { get; }

        internal RecordingHourReader HourReader { get; }

        internal RecordingProjectionReader ProjectionReader { get; }

        internal RecordingProjectionWriter Writer { get; }

        internal RecordingCheckpoint Checkpoint { get; }

        internal RecordingJobLock JobLock { get; }

        internal UsagePeriodProjectionRebuilder Rebuilder { get; }

        internal ValueTask<BoundedUsagePeriodRebuildResult> RebuildAsync(
            BoundedUsagePeriodRebuildRequest request) => Rebuilder.RebuildAsync(
                JobLock,
                request,
                TestToken);
    }

    private sealed class RecordingUnitOfWorkFactory(List<string> operations) :
        IUnitOfWorkFactory
    {
        private readonly List<string> _operations = operations;
        private int _activeCount;

        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal int MaximumActiveCount { get; private set; }

        internal List<RecordingContext> Contexts { get; } = [];

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, _activeCount);
            _activeCount++;
            MaximumActiveCount = Math.Max(MaximumActiveCount, _activeCount);
            int id = ++BeginCalls;
            RecordingContext context = new(id);
            Contexts.Add(context);
            _operations.Add($"begin:{id}");
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this, context));
        }

        private sealed class UnitOfWork(
            RecordingUnitOfWorkFactory owner,
            RecordingContext context) : IUnitOfWork
        {
            private bool _committed;
            private bool _disposed;

            public IUnitOfWorkContext Context => context;

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.False(_committed);
                Assert.False(_disposed);
                foreach (Action action in context.CommitActions)
                {
                    action();
                }

                _committed = true;
                owner.CommitCalls++;
                owner._operations.Add($"commit:{context.Id}");
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                Assert.False(_disposed);
                _disposed = true;
                owner._activeCount--;
                owner.DisposeCalls++;
                owner._operations.Add($"dispose:{context.Id}");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingContext(int id) : IUnitOfWorkContext
    {
        internal int Id { get; } = id;

        internal List<Action> CommitActions { get; } = [];
    }

    private sealed class RecordingFactReader(
        GroupQuotaReconciliationFactSnapshot? snapshot,
        List<string> operations) : IGroupQuotaReconciliationFactReader
    {
        internal GroupQuotaReconciliationFactSnapshot? Snapshot { get; set; } = snapshot;

        internal int ReadCalls { get; private set; }

        public ValueTask<EntityId?> ResolvePeriodAsync(
            EntityId groupId,
            EntityId? periodId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
            EntityId groupId,
            EntityId? periodId,
            long checkpointSourceEventSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Assert.IsType<RecordingContext>(unitOfWorkContext);
            Assert.Equal(GroupId, groupId);
            Assert.Equal(PeriodId, periodId);
            ReadCalls++;
            operations.Add($"authoritative:{checkpointSourceEventSequence}");
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask<IReadOnlyList<GroupQuotaReconciliationCandidate>>
            ListCurrentCandidatesAsync(
                EntityId? afterGroupId,
                int maximumCount,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<long>>
            ListPeriodSourceEventSequencesAsync(
                EntityId groupId,
                EntityId periodId,
                long throughSourceEventSequence,
                long afterSourceEventSequence,
                int maximumCount,
                IUnitOfWorkContext unitOfWorkContext,
                CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingHourReader(List<string> operations) :
        IBoundedUsageRebuildFactReader
    {
        private readonly Dictionary<DateTimeOffset, IReadOnlyList<AttemptSettlementFact>>
            _facts = [];

        internal List<HourRead> Calls { get; } = [];

        internal void Set(
            DateTimeOffset bucketStart,
            IReadOnlyList<AttemptSettlementFact> facts) => _facts[bucketStart] = facts;

        public ValueTask<BoundedUsageRebuildHourSnapshot> ReadHourAsync(
            EntityId groupId,
            EntityId periodId,
            DateTimeOffset bucketStart,
            long checkpointSourceSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingContext context = Assert.IsType<RecordingContext>(unitOfWorkContext);
            _facts.TryGetValue(bucketStart, out IReadOnlyList<AttemptSettlementFact>? facts);
            Calls.Add(new HourRead(bucketStart, context));
            operations.Add($"hour:{bucketStart:O}:{checkpointSourceSequence}");
            return ValueTask.FromResult(new BoundedUsageRebuildHourSnapshot(
                groupId,
                periodId,
                bucketStart,
                checkpointSourceSequence,
                facts ?? []));
        }
    }

    private sealed class RecordingProjectionReader(
        RecordingProjectionWriter writer,
        List<string> operations) : IUsageReconciliationProjectionReader
    {
        internal List<RecordingContext> Contexts { get; } = [];

        public ValueTask<UsageReconciliationProjectionSnapshot> ReadAsync(
            EntityId groupId,
            EntityId periodId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingContext context = Assert.IsType<RecordingContext>(unitOfWorkContext);
            Contexts.Add(context);
            operations.Add("projection:17");
            BigInteger projected = writer.Committed.Values.Aggregate(
                BigInteger.Zero,
                static (total, projection) => total + projection.Group.TotalTokens);
            return ValueTask.FromResult(new UsageReconciliationProjectionSnapshot(
                groupId,
                periodId,
                projected,
                CheckpointSourceEventSequence: 17,
                DataThrough: FirstHour,
                CheckedAt: FirstHour.AddHours(1)));
        }
    }

    private sealed class RecordingProjectionWriter(List<string> operations) :
        IBoundedUsageProjectionWriter
    {
        internal Dictionary<DateTimeOffset, UsageHourProjection> Committed { get; } = [];

        internal List<ProjectionWrite> Calls { get; } = [];

        internal void Seed(UsageHourProjection projection) =>
            Committed[projection.BucketStart] = projection;

        public ValueTask ReplaceOrDeleteAsync(
            EntityId groupId,
            EntityId periodId,
            DateTimeOffset bucketStart,
            UsageHourProjection? projection,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingContext context = Assert.IsType<RecordingContext>(unitOfWorkContext);
            Calls.Add(new ProjectionWrite(bucketStart, projection, context));
            operations.Add($"write:{bucketStart:O}:{(projection is null ? "delete" : "value")}");
            context.CommitActions.Add(() =>
            {
                if (projection is null)
                {
                    _ = Committed.Remove(bucketStart);
                }
                else
                {
                    Committed[bucketStart] = projection;
                }
            });
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCheckpoint(
        long lastEventSequence,
        IEnumerable<bool>? heartbeats,
        List<string> operations) : IUsageAggregationCheckpoint
    {
        private readonly Queue<bool> _heartbeats = new(heartbeats ?? [true, true, true]);

        internal UsageAggregationClaimResult? ClaimResult { get; set; }

        internal bool ReleaseResult { get; set; } = true;

        internal int ClaimCalls { get; private set; }

        internal int HeartbeatCalls { get; private set; }

        internal List<RecordingContext> HeartbeatContexts { get; } = [];

        internal int AdvanceCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal long LastEventSequence { get; } = lastEventSequence;

        public ValueTask<UsageAggregationClaimResult> ClaimAsync(
            UsageAggregationClaimRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Assert.IsType<RecordingContext>(unitOfWorkContext);
            ClaimCalls++;
            operations.Add($"checkpoint:claim:{LastEventSequence}");
            return ValueTask.FromResult(ClaimResult ?? UsageAggregationClaimResult.Acquired(
                new UsageAggregationLease(
                    request.ProjectorName,
                    request.PartitionKey,
                    request.Owner,
                    Version: 1,
                    LastEventSequence,
                    CompletedThrough: FirstHour)));
        }

        public ValueTask<bool> HeartbeatAsync(
            UsageAggregationLease lease,
            TimeSpan leaseDuration,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordingContext context = Assert.IsType<RecordingContext>(unitOfWorkContext);
            HeartbeatCalls++;
            HeartbeatContexts.Add(context);
            bool result = _heartbeats.Count == 0 || _heartbeats.Dequeue();
            operations.Add($"checkpoint:heartbeat:{result}");
            return ValueTask.FromResult(result);
        }

        public ValueTask<UsageAggregationLease?> AdvanceAsync(
            UsageAggregationAdvanceRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            AdvanceCalls++;
            return ValueTask.FromResult<UsageAggregationLease?>(request.Lease);
        }

        public ValueTask<bool> ReleaseAsync(
            UsageAggregationLease lease,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Assert.IsType<RecordingContext>(unitOfWorkContext);
            ReleaseCalls++;
            operations.Add("checkpoint:release");
            return ValueTask.FromResult(ReleaseResult);
        }
    }

    private sealed class RecordingJobLock(
        WorkerJobIdentity job,
        RecordingUnitOfWorkFactory? unitOfWorkFactory,
        bool[] ownership,
        bool[] fencedUnits) : IWorkerSessionLock
    {
        private readonly Queue<bool> _ownership = new(ownership);
        private readonly Queue<bool> _fencedUnits = new(fencedUnits);

        public WorkerJobIdentity Job { get; } = job;

        public long LockId { get; } = 0x1234;

        internal int VerifyCalls { get; private set; }

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(_ownership.Count == 0 || _ownership.Dequeue());
        }

        public ValueTask<IUnitOfWork?> TryBeginFencedUnitOfWorkAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_fencedUnits.Count > 0 && !_fencedUnits.Dequeue())
            {
                return ValueTask.FromResult<IUnitOfWork?>(null);
            }

            return unitOfWorkFactory is null
                ? ValueTask.FromResult<IUnitOfWork?>(null)
                : BeginAsync(unitOfWorkFactory, cancellationToken);
        }

        private static async ValueTask<IUnitOfWork?> BeginAsync(
            RecordingUnitOfWorkFactory unitOfWorkFactory,
            CancellationToken cancellationToken) => await unitOfWorkFactory
                .BeginAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record HourRead(
        DateTimeOffset BucketStart,
        RecordingContext Context);

    private sealed record ProjectionWrite(
        DateTimeOffset BucketStart,
        UsageHourProjection? Projection,
        RecordingContext Context);
}
