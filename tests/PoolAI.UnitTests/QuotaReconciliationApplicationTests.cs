using System.Globalization;
using System.Numerics;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Usage.Application;
using PoolAI.Modules.Usage.Application.Ports;

namespace PoolAI.UnitTests;

public sealed class QuotaReconciliationApplicationTests
{
    private static readonly EntityId GroupId = Id("10000000-0000-0000-0000-000000000001");
    private static readonly EntityId OtherGroupId = Id("10000000-0000-0000-0000-000000000002");
    private static readonly EntityId PeriodId = Id("20000000-0000-0000-0000-000000000001");
    private static readonly EntityId OtherPeriodId = Id("20000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset CheckedAt = new(
        2026,
        8,
        3,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(12, 120, 120, 12, 12, true, true,
        UsageProjectionReconciliationStatus.Reconciled)]
    [InlineData(10, 100, 100, 12, 12, true, true,
        UsageProjectionReconciliationStatus.Lagging)]
    [InlineData(10, 100, 99, 12, 12, true, true,
        UsageProjectionReconciliationStatus.Mismatched)]
    [InlineData(10, 100, 100, 12, 12, false, true,
        UsageProjectionReconciliationStatus.Blocked)]
    [InlineData(13, 130, 130, 12, 12, true, true,
        UsageProjectionReconciliationStatus.Blocked)]
    [InlineData(0, 0, 1, 12, 12, true, true,
        UsageProjectionReconciliationStatus.Blocked)]
    [InlineData(0, 0, 0, 12, 12, true, true,
        UsageProjectionReconciliationStatus.NotStarted)]
    internal void CalculatorClassifiesCheckpointAlignedProjectionWithFrozenPrecedence(
        long checkpoint,
        long expectedConsumed,
        long projectedConsumed,
        long latestPeriodSequence,
        long latestGroupSequence,
        bool eventChainConsistent,
        bool latestEventMatchesLedger,
        UsageProjectionReconciliationStatus expectedStatus)
    {
        QuotaReconciliationView result = QuotaReconciliationCalculator.Calculate(
            Fact(
                checkpoint: checkpoint,
                expectedConsumedAtCheckpoint: new BigInteger(expectedConsumed),
                latestPeriodEventSequence: latestPeriodSequence,
                latestGroupEventSequence: latestGroupSequence,
                eventChainConsistent: eventChainConsistent,
                latestEventMatchesLedger: latestEventMatchesLedger),
            Projection(
                checkpoint: checkpoint,
                projectedConsumedTokens: new BigInteger(projectedConsumed)));

        Assert.Equal(expectedStatus, result.UsageProjection.Status);
        Assert.Equal(
            new BigInteger(expectedConsumed - projectedConsumed),
            result.UsageProjection.ConsumedVariance);
    }

    [Fact]
    public void HistoricalPeriodCanReconcileBehindTheLatestGroupSequence()
    {
        QuotaReconciliationView result = QuotaReconciliationCalculator.Calculate(
            Fact(
                checkpoint: 17,
                expectedConsumedAtCheckpoint: new BigInteger(400),
                latestPeriodEventSequence: 17,
                latestGroupEventSequence: 29,
                isCurrentPeriod: false),
            Projection(
                checkpoint: 17,
                projectedConsumedTokens: new BigInteger(400)));

        Assert.Equal(
            UsageProjectionReconciliationStatus.Reconciled,
            result.UsageProjection.Status);
        Assert.Equal(17, result.UsageProjection.LatestSourceEventSequence);
        Assert.Equal(17, result.UsageProjection.CheckpointSourceEventSequence);
    }

    [Fact]
    public void CheckpointMustBelongToTheSameGroup()
    {
        QuotaReconciliationView result = QuotaReconciliationCalculator.Calculate(
            Fact(checkpointBelongsToGroup: false),
            Projection());

        Assert.Equal(
            UsageProjectionReconciliationStatus.Blocked,
            result.UsageProjection.Status);
    }

    [Fact]
    public void CalculatorKeepsSeventyEightDigitArithmeticExact()
    {
        BigInteger maximum = BigInteger.Parse(
            new string('9', 78),
            CultureInfo.InvariantCulture);

        QuotaReconciliationView result = QuotaReconciliationCalculator.Calculate(
            Fact(
                checkpoint: 1,
                ledgerTotalTokens: maximum,
                ledgerConsumedTokens: maximum,
                ledgerReservedTokens: BigInteger.Zero,
                factConsumedTokens: BigInteger.Zero,
                pendingReservationTokens: maximum,
                expectedConsumedAtCheckpoint: maximum,
                latestPeriodEventSequence: 1,
                latestGroupEventSequence: 1),
            Projection(
                checkpoint: 1,
                projectedConsumedTokens: BigInteger.Zero));

        Assert.Equal(maximum, result.ConsumedVariance);
        Assert.Equal(BigInteger.Negate(maximum), result.ReservedVariance);
        Assert.Equal(maximum, result.UsageProjection.ExpectedConsumedTokens);
        Assert.Equal(maximum, result.UsageProjection.ConsumedVariance);
        Assert.Equal(
            UsageProjectionReconciliationStatus.Blocked,
            result.UsageProjection.Status);
    }

    [Fact]
    public void CalculatorRejectsSnapshotsWithDifferentIdentityOrCheckpoint()
    {
        Assert.Throws<InvalidOperationException>(() =>
            QuotaReconciliationCalculator.Calculate(
                Fact(),
                Projection(groupId: OtherGroupId)));
        Assert.Throws<InvalidOperationException>(() =>
            QuotaReconciliationCalculator.Calculate(
                Fact(),
                Projection(periodId: OtherPeriodId)));
        Assert.Throws<InvalidOperationException>(() =>
            QuotaReconciliationCalculator.Calculate(
                Fact(checkpoint: 12),
                Projection(checkpoint: 11)));
    }

    [Fact]
    public async Task ServiceReadsIdentityProjectionAndExactFactsInThreeIndependentUnits()
    {
        List<string> operations = [];
        GroupQuotaReconciliationFactSnapshot identity = Fact(checkpoint: 0);
        GroupQuotaReconciliationFactSnapshot exact = Fact(
            checkpoint: 17,
            expectedConsumedAtCheckpoint: new BigInteger(400),
            latestPeriodEventSequence: 17,
            latestGroupEventSequence: 17);
        UsageReconciliationProjectionSnapshot projection = Projection(
            checkpoint: 17,
            projectedConsumedTokens: new BigInteger(400));
        RecordingUnitOfWorkFactory unitOfWorkFactory = new(operations);
        RecordingFactReader factReader = new([identity, exact], operations);
        RecordingProjectionReader projectionReader = new(projection, operations);
        QuotaReconciliationService service = new(
            unitOfWorkFactory,
            factReader,
            projectionReader);

        Result<QuotaReconciliationView> result = await service.ExecuteAsync(
            GroupId,
            periodId: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            UsageProjectionReconciliationStatus.Reconciled,
            result.Value.UsageProjection.Status);
        Assert.Equal(
            [
                "begin:1",
                "fact:0:uow:1",
                "commit:1",
                "dispose:1",
                "begin:2",
                "projection:17:uow:2",
                "commit:2",
                "dispose:2",
                "begin:3",
                "fact:17:uow:3",
                "commit:3",
                "dispose:3",
            ],
            operations);
        Assert.Equal(3, unitOfWorkFactory.BeginCalls);
        Assert.Equal(3, unitOfWorkFactory.CommitCalls);
        Assert.Equal(3, unitOfWorkFactory.DisposeCalls);
        Assert.NotSame(unitOfWorkFactory.Contexts[0], unitOfWorkFactory.Contexts[1]);
        Assert.NotSame(unitOfWorkFactory.Contexts[1], unitOfWorkFactory.Contexts[2]);
        Assert.Null(factReader.Calls[0].PeriodId);
        Assert.Equal(0, factReader.Calls[0].CheckpointSourceEventSequence);
        Assert.Same(unitOfWorkFactory.Contexts[0], factReader.Calls[0].Context);
        Assert.Equal(PeriodId, projectionReader.Calls[0].PeriodId);
        Assert.Same(unitOfWorkFactory.Contexts[1], projectionReader.Calls[0].Context);
        Assert.Equal(PeriodId, factReader.Calls[1].PeriodId);
        Assert.Equal(17, factReader.Calls[1].CheckpointSourceEventSequence);
        Assert.Same(unitOfWorkFactory.Contexts[2], factReader.Calls[1].Context);
    }

    [Fact]
    public async Task ServiceReturnsResourceNotFoundWithoutReadingProjectionWhenIdentityIsAbsent()
    {
        List<string> operations = [];
        RecordingUnitOfWorkFactory unitOfWorkFactory = new(operations);
        RecordingFactReader factReader = new([null], operations);
        RecordingProjectionReader projectionReader = new(Projection(), operations);
        QuotaReconciliationService service = new(
            unitOfWorkFactory,
            factReader,
            projectionReader);

        Result<QuotaReconciliationView> result = await service.ExecuteAsync(
            GroupId,
            OtherPeriodId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("resource_not_found", result.Error.Code);
        Assert.Equal(
            "The requested Group quota period was not found.",
            result.Error.Description);
        Assert.Equal(
            ["begin:1", "fact:0:uow:1", "commit:1", "dispose:1"],
            operations);
        Assert.Equal(1, unitOfWorkFactory.BeginCalls);
        Assert.Equal(1, unitOfWorkFactory.CommitCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
        Assert.Empty(projectionReader.Calls);
        Assert.Equal(OtherPeriodId, factReader.Calls[0].PeriodId);
    }

    [Fact]
    public async Task ServiceReturnsResourceNotFoundWhenExactFactReadNoLongerResolves()
    {
        List<string> operations = [];
        GroupQuotaReconciliationFactSnapshot identity = Fact(checkpoint: 0);
        UsageReconciliationProjectionSnapshot projection = Projection(checkpoint: 17);
        RecordingUnitOfWorkFactory unitOfWorkFactory = new(operations);
        RecordingFactReader factReader = new([identity, null], operations);
        RecordingProjectionReader projectionReader = new(projection, operations);
        QuotaReconciliationService service = new(
            unitOfWorkFactory,
            factReader,
            projectionReader);

        Result<QuotaReconciliationView> result = await service.ExecuteAsync(
            GroupId,
            periodId: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("resource_not_found", result.Error.Code);
        Assert.Equal(
            [
                "begin:1",
                "fact:0:uow:1",
                "commit:1",
                "dispose:1",
                "begin:2",
                "projection:17:uow:2",
                "commit:2",
                "dispose:2",
                "begin:3",
                "fact:17:uow:3",
                "commit:3",
                "dispose:3",
            ],
            operations);
        Assert.Equal(3, unitOfWorkFactory.BeginCalls);
        Assert.Equal(3, unitOfWorkFactory.CommitCalls);
        Assert.Equal(3, unitOfWorkFactory.DisposeCalls);
    }

    private static GroupQuotaReconciliationFactSnapshot Fact(
        long checkpoint = 12,
        BigInteger? ledgerTotalTokens = null,
        BigInteger? ledgerConsumedTokens = null,
        BigInteger? ledgerReservedTokens = null,
        BigInteger? factConsumedTokens = null,
        BigInteger? pendingReservationTokens = null,
        BigInteger? expectedConsumedAtCheckpoint = null,
        long latestPeriodEventSequence = 12,
        long latestGroupEventSequence = 12,
        bool eventChainConsistent = true,
        bool latestEventMatchesLedger = true,
        bool isCurrentPeriod = true,
        bool checkpointBelongsToGroup = true)
    {
        BigInteger ledgerConsumed = ledgerConsumedTokens ?? new BigInteger(100);
        BigInteger ledgerReserved = ledgerReservedTokens ?? new BigInteger(20);
        return new GroupQuotaReconciliationFactSnapshot(
            GroupId: GroupId,
            PeriodId: PeriodId,
            CheckpointSourceEventSequence: checkpoint,
            LedgerTotalTokens: ledgerTotalTokens ?? new BigInteger(1_000),
            LedgerConsumedTokens: ledgerConsumed,
            LedgerReservedTokens: ledgerReserved,
            FactConsumedTokens: factConsumedTokens ?? ledgerConsumed,
            PendingReservationTokens: pendingReservationTokens ?? ledgerReserved,
            PendingReservationCount: ledgerReserved.IsZero ? 0 : 1,
            OverdueReservationCount: 0,
            OldestOverdueAt: null,
            ExpectedConsumedAtCheckpoint: expectedConsumedAtCheckpoint ?? ledgerConsumed,
            CheckpointBelongsToGroup: checkpointBelongsToGroup,
            LatestPeriodEventSequence: latestPeriodEventSequence,
            LatestPeriodEventOccurredAt: CheckedAt.AddMinutes(-1),
            EventChainConsistent: eventChainConsistent,
            FactEventCoverageConsistent: true,
            LatestEventMatchesLedger: latestEventMatchesLedger,
            OverageTokens: BigInteger.Zero,
            CheckedAt: CheckedAt,
            IsCurrentPeriod: isCurrentPeriod,
            FirstPeriodEventSequence: 1,
            LatestGroupEventSequence: latestGroupEventSequence,
            PeriodEventCount: latestPeriodEventSequence);
    }

    private static UsageReconciliationProjectionSnapshot Projection(
        long checkpoint = 12,
        BigInteger? projectedConsumedTokens = null,
        EntityId? groupId = null,
        EntityId? periodId = null) => new(
            groupId ?? GroupId,
            periodId ?? PeriodId,
            projectedConsumedTokens ?? new BigInteger(100),
            checkpoint,
            CheckedAt.AddMinutes(-2),
            CheckedAt);

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed record FactReadCall(
        EntityId GroupId,
        EntityId? PeriodId,
        long CheckpointSourceEventSequence,
        IUnitOfWorkContext Context);

    private sealed record ProjectionReadCall(
        EntityId GroupId,
        EntityId PeriodId,
        IUnitOfWorkContext Context);

    private sealed class RecordingFactReader(
        IEnumerable<GroupQuotaReconciliationFactSnapshot?> snapshots,
        ICollection<string> operations) : IGroupQuotaReconciliationFactReader
    {
        private readonly Queue<GroupQuotaReconciliationFactSnapshot?> _snapshots = new(
            snapshots);
        private readonly ICollection<string> _operations = operations;

        internal List<FactReadCall> Calls { get; } = [];

        public ValueTask<GroupQuotaReconciliationFactSnapshot?> ReadAsync(
            EntityId groupId,
            EntityId? periodId,
            long checkpointSourceEventSequence,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = Assert.IsType<TestUnitOfWorkContext>(
                unitOfWorkContext);
            _operations.Add(
                $"fact:{checkpointSourceEventSequence}:uow:{context.Sequence}");
            Calls.Add(new FactReadCall(
                groupId,
                periodId,
                checkpointSourceEventSequence,
                unitOfWorkContext));
            if (_snapshots.Count == 0)
            {
                throw new InvalidOperationException("No fact snapshot was configured.");
            }

            return ValueTask.FromResult(_snapshots.Dequeue());
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

    private sealed class RecordingProjectionReader(
        UsageReconciliationProjectionSnapshot snapshot,
        ICollection<string> operations) : IUsageReconciliationProjectionReader
    {
        private readonly ICollection<string> _operations = operations;

        internal List<ProjectionReadCall> Calls { get; } = [];

        public ValueTask<UsageReconciliationProjectionSnapshot> ReadAsync(
            EntityId groupId,
            EntityId periodId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestUnitOfWorkContext context = Assert.IsType<TestUnitOfWorkContext>(
                unitOfWorkContext);
            _operations.Add(
                $"projection:{snapshot.CheckpointSourceEventSequence}:uow:{context.Sequence}");
            Calls.Add(new ProjectionReadCall(groupId, periodId, unitOfWorkContext));
            return ValueTask.FromResult(snapshot);
        }
    }

    private sealed class RecordingUnitOfWorkFactory(
        ICollection<string> operations) : IUnitOfWorkFactory
    {
        private readonly ICollection<string> _operations = operations;

        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal List<IUnitOfWorkContext> Contexts { get; } = [];

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sequence = ++BeginCalls;
            TestUnitOfWorkContext context = new(sequence);
            Contexts.Add(context);
            _operations.Add($"begin:{sequence}");
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(
                this,
                context,
                sequence));
        }

        private sealed class UnitOfWork(
            RecordingUnitOfWorkFactory owner,
            IUnitOfWorkContext context,
            int sequence) : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = context;

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                owner._operations.Add($"commit:{sequence}");
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                owner._operations.Add($"dispose:{sequence}");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record TestUnitOfWorkContext(int Sequence) : IUnitOfWorkContext;
}
