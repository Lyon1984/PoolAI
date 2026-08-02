#pragma warning disable MA0051 // The test keeps the complete short-UoW worker protocol visible.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/database/README.md, expiry keyset and renew/settle race semantics.
// - docs/开发执行规格-v1.0.md, DEC-032 and M3-E3 reservation sweeper rules.
public sealed class ReservationSweeperProcessorTests
{
    private static readonly DateTimeOffset DueAt = new(
        2026,
        8,
        1,
        5,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void DependenciesAreRequired()
    {
        ScriptedQuotaLedgerRepository repository = new([], []);
        RecordingUnitOfWorkFactory units = new();

        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperProcessor(
                null!,
                units,
                NoOpOperationalEventWriter.Instance,
                NoOpIdempotentAuditAppender.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperProcessor(
                repository,
                null!,
                NoOpOperationalEventWriter.Instance,
                NoOpIdempotentAuditAppender.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperProcessor(
                repository,
                units,
                null!,
                NoOpIdempotentAuditAppender.Instance));
        ArgumentNullException auditException = Assert.Throws<ArgumentNullException>(
            () => new ReservationSweeperProcessor(
                repository,
                units,
                NoOpOperationalEventWriter.Instance,
                null!));
        Assert.Equal("idempotentAuditAppender", auditException.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task PageSizeMustStayInsideTheBoundedSelectorContract(int pageSize)
    {
        ReservationSweeperProcessor processor = new(
            new ScriptedQuotaLedgerRepository([], []),
            new RecordingUnitOfWorkFactory(),
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        _ = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => processor.ProcessAsync(
                new ScriptedJobLock(),
                pageSize,
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task JobLockIsRequired()
    {
        ReservationSweeperProcessor processor = new(
            new ScriptedQuotaLedgerRepository([], []),
            new RecordingUnitOfWorkFactory(),
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        _ = await Assert.ThrowsAsync<ArgumentNullException>(
            () => processor.ProcessAsync(
                null!,
                pageSize: 1,
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task CancellationBeforeTheRoundStartsDoesNotTouchTheLockOrDatabase()
    {
        RecordingUnitOfWorkFactory units = new();
        ScriptedJobLock jobLock = new();
        ReservationSweeperProcessor processor = new(
            new ScriptedQuotaLedgerRepository([], []),
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessAsync(
                jobLock,
                pageSize: 1,
                cancellation.Token).AsTask());

        Assert.Equal(0, jobLock.VerificationCalls);
        Assert.Equal(0, units.BeginCalls);
    }

    [Fact]
    public async Task OwnershipLossAtRoundStartDoesNotOpenAUnitOfWork()
    {
        ScriptedQuotaLedgerRepository repository = new([], []);
        RecordingUnitOfWorkFactory units = new();
        ScriptedJobLock jobLock = new(false);
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        ReservationSweepProcessResult result = await processor.ProcessAsync(
            jobLock,
            pageSize: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ReservationSweepProcessDisposition.OwnershipLost,
            result.Disposition);
        Assert.Equal(0, result.PageCount);
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(1, jobLock.VerificationCalls);
        Assert.Equal(0, repository.PageCalls);
        Assert.Equal(0, units.BeginCalls);
    }

    [Fact]
    public async Task EmptyFirstPageCompletesWithoutAnExpiryUnitOfWork()
    {
        ScriptedQuotaLedgerRepository repository = new([[]], []);
        RecordingUnitOfWorkFactory units = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        ReservationSweepProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(true),
            pageSize: 1000,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReservationSweepProcessDisposition.Completed, result.Disposition);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(0, result.ScannedCount);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
    }

    [Fact]
    public async Task OneSessionLockGuardsBoundedKeysetPagesAndEachExpiryUsesAShortUnitOfWork()
    {
        QuotaExpiryCandidate first = Candidate(1, DueAt);
        QuotaExpiryCandidate second = Candidate(2, DueAt);
        QuotaExpiryCandidate third = Candidate(3, DueAt.AddSeconds(1));
        ScriptedQuotaLedgerRepository repository = new(
            pages:
            [
                [first, second],
                [third],
            ],
            expiryResults:
            [
                QuotaLedgerFailure.None,
                QuotaLedgerFailure.ReservationExpiryRaceLost,
                QuotaLedgerFailure.None,
            ]);
        RecordingUnitOfWorkFactory units = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);
        ScriptedJobLock jobLock = new(true, true, true, true, true);

        ReservationSweepProcessResult result = await processor.ProcessAsync(
            jobLock,
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReservationSweepProcessDisposition.Completed, result.Disposition);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(3, result.ScannedCount);
        Assert.Equal(2, result.ExpiredCount);
        Assert.Equal(1, result.RaceLostCount);
        Assert.Equal(5, jobLock.VerificationCalls);
        Assert.Equal(2, repository.PageCalls);
        Assert.Null(repository.Cursors[0]);
        Assert.Equal(second.Key, repository.Cursors[1]);
        Assert.Equal(3, repository.ExpiryCalls);
        Assert.Equal(5, units.BeginCalls);
        Assert.Equal(4, units.CommitCalls);
        Assert.Equal(5, units.DisposeCalls);
        Assert.Equal(3, repository.ExpiryContexts.Distinct().Count());
        Assert.DoesNotContain(
            repository.ExpiryContexts[0],
            repository.PageContexts);
        Assert.All(
            repository.ExpiryWrites,
            static write =>
            {
                Assert.Equal(
                    $"quota:expire:v1:{write.Candidate.AttemptId.Value:N}",
                    write.Mutation.IdempotencyKey);
                Assert.Equal("reservation_lease_expired", write.Reason);
            });
    }

    [Fact]
    public async Task ConservativeExpiryAppendsOneBoundedServiceAuditBeforeCommit()
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        AttemptSettlementFact fact = ConservativeFact(candidate);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.None],
            settlementFacts: [fact]);
        RecordingUnitOfWorkFactory units = new();
        RecordingIdempotentAuditAppender audit = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            audit);

        ReservationSweepProcessResult result = await processor.ProcessAsync(
            new ScriptedJobLock(true, true),
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReservationSweepProcessDisposition.Completed, result.Disposition);
        AuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActorType.Service, entry.ActorType);
        Assert.Equal("group_quota.attempt_fact_conservative_expired", entry.Action);
        Assert.Equal("usage_attempt", entry.TargetType);
        Assert.Equal(candidate.AttemptId, entry.TargetId);
        Assert.Equal(fact.RequestId, entry.RequestId);
        Assert.Equal(candidate.GroupId.Value, entry.Metadata.GetProperty("group_id").GetGuid());
        Assert.Equal(
            "conservative_estimate",
            entry.Metadata.GetProperty("usage_source").GetString());
        Assert.Equal(
            "100",
            entry.AfterState!.Value.GetProperty("total_tokens").GetString());
        Assert.Same(repository.ExpiryContexts.Single(), audit.Contexts.Single());
        Assert.Equal(2, units.CommitCalls);
    }

    [Fact]
    public async Task ConservativeExpiryAuditFailureRollsBackTheFactTransaction()
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.None],
            settlementFacts: [ConservativeFact(candidate)]);
        RecordingUnitOfWorkFactory units = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            new RecordingIdempotentAuditAppender(throwOnAppend: true));

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(
                new ScriptedJobLock(true, true),
                pageSize: 2,
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(2, units.DisposeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ConservativeExpiryRejectsEveryContradictoryTerminalFactField(
        int contradiction)
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        AttemptSettlementFact valid = ConservativeFact(candidate);
        AttemptSettlementFact contradictory = contradiction switch
        {
            0 => valid with { AttemptId = Id(901) },
            1 => valid with { ReservationId = Id(902) },
            2 => valid with { GroupId = Id(903) },
            3 => valid with { PeriodId = Id(904) },
            4 => valid with
            {
                Usage = valid.Usage with { Source = SettlementUsageSource.Upstream },
            },
            _ => valid with
            {
                Usage = valid.Usage with { IsEstimated = false },
            },
        };
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.None],
            settlementFacts: [contradictory]);
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new(static () => { });
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            events,
            NoOpIdempotentAuditAppender.Instance);

        ReservationSweepFailureException exception = await Assert.ThrowsAsync<
            ReservationSweepFailureException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true, true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(QuotaLedgerFailure.TerminalFactInvariantBroken, exception.Failure);
        Assert.Equal(1, events.WriteCount);
        Assert.Equal(
            "group_quota.reservation_sweeper_invariant_violation",
            events.EventName);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(2, units.DisposeCalls);
    }

    [Fact]
    public async Task OwnershipLossBeforeACandidateStopsWithoutOpeningAnotherUnitOfWork()
    {
        QuotaExpiryCandidate first = Candidate(1, DueAt);
        QuotaExpiryCandidate second = Candidate(2, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[first, second]],
            expiryResults: [QuotaLedgerFailure.None]);
        RecordingUnitOfWorkFactory units = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);
        ScriptedJobLock jobLock = new(true, true, false);

        ReservationSweepProcessResult result = await processor.ProcessAsync(
            jobLock,
            pageSize: 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ReservationSweepProcessDisposition.OwnershipLost,
            result.Disposition);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(1, result.ExpiredCount);
        Assert.Equal(1, repository.ExpiryCalls);
        Assert.Equal(2, units.BeginCalls);
        Assert.Equal(2, units.CommitCalls);
        Assert.Equal(2, units.DisposeCalls);
    }

    [Fact]
    public async Task NumericOverflowRollsBackBeforePublishingTheP0Event()
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.TokenNumericOverflow]);
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new(() =>
        {
            Assert.Equal(2, units.BeginCalls);
            Assert.Equal(1, units.CommitCalls);
            Assert.Equal(2, units.DisposeCalls);
        });
        ReservationSweeperProcessor processor = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        ReservationSweepFailureException exception = await Assert.ThrowsAsync<
            ReservationSweepFailureException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true, true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(QuotaLedgerFailure.TokenNumericOverflow, exception.Failure);
        Assert.Equal("group_quota.token_numeric_overflow", events.EventName);
        Assert.Equal("P0", events.Payload.GetProperty("severity").GetString());
        Assert.Equal("expire", events.Payload.GetProperty("operation").GetString());
        Assert.Equal(
            candidate.AttemptId.Value,
            events.Payload.GetProperty("attempt_id").GetGuid());
        Assert.False(events.CancellationToken.CanBeCanceled);
    }

    [Fact]
    public async Task DependencyFailureRollsBackWithoutPublishingAnInvariantEvent()
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.DependencyUnavailable]);
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new(static () => { });
        ReservationSweeperProcessor processor = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        ReservationSweepFailureException exception = await Assert.ThrowsAsync<
            ReservationSweepFailureException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true, true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(QuotaLedgerFailure.DependencyUnavailable, exception.Failure);
        Assert.Equal(0, events.WriteCount);
        Assert.Equal(2, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(2, units.DisposeCalls);
    }

    [Fact]
    public async Task UnexpectedLedgerFailurePublishesTheGenericP0InvariantEvent()
    {
        QuotaExpiryCandidate candidate = Candidate(1, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[candidate]],
            expiryResults: [QuotaLedgerFailure.Internal]);
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new(static () => { });
        ReservationSweeperProcessor processor = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        ReservationSweepFailureException exception = await Assert.ThrowsAsync<
            ReservationSweepFailureException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true, true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(QuotaLedgerFailure.Internal, exception.Failure);
        Assert.Equal(1, events.WriteCount);
        Assert.Equal(
            "group_quota.reservation_sweeper_invariant_violation",
            events.EventName);
        Assert.Equal("P0", events.Payload.GetProperty("severity").GetString());
        Assert.Equal("Internal", events.Payload.GetProperty("classification").GetString());
        Assert.Equal(
            candidate.AttemptId.Value,
            events.Payload.GetProperty("attempt_id").GetGuid());
        Assert.False(events.CancellationToken.CanBeCanceled);
        Assert.Equal(2, units.DisposeCalls);
    }

    [Fact]
    public async Task SelectorCannotReturnMoreRowsThanRequested()
    {
        ScriptedQuotaLedgerRepository repository = new(
            pages: [[Candidate(1, DueAt), Candidate(2, DueAt)]],
            expiryResults: []);
        RecordingUnitOfWorkFactory units = new();
        ReservationSweeperProcessor processor = new(
            repository,
            units,
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The reservation expiry selector exceeded its page bound.",
            exception.Message);
        Assert.Equal(0, repository.ExpiryCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SelectorRejectsEveryInvalidIdentityPosition(int identityPosition)
    {
        QuotaExpiryCandidate valid = Candidate(1, DueAt);
        EntityId invalid = new(Guid.Parse(
            "018f3a4b-5c6d-4e8f-9123-000000000999"));
        QuotaExpiryCandidate candidate = identityPosition switch
        {
            0 => valid with { ReservationId = invalid },
            1 => valid with { AttemptId = invalid },
            2 => valid with { GroupId = invalid },
            _ => valid with { PeriodId = invalid },
        };
        ScriptedQuotaLedgerRepository repository = new([[candidate]], []);
        ReservationSweeperProcessor processor = new(
            repository,
            new RecordingUnitOfWorkFactory(),
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true),
                    pageSize: 1,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The reservation expiry selector returned an invalid identity.",
            exception.Message);
        Assert.Equal(0, repository.ExpiryCalls);
    }

    [Fact]
    public async Task SelectorCannotMoveAtOrBehindThePriorPageCursor()
    {
        QuotaExpiryCandidate first = Candidate(1, DueAt);
        QuotaExpiryCandidate second = Candidate(2, DueAt);
        ScriptedQuotaLedgerRepository repository = new(
            pages:
            [
                [first, second],
                [first],
            ],
            expiryResults:
            [
                QuotaLedgerFailure.None,
                QuotaLedgerFailure.None,
            ]);
        ReservationSweeperProcessor processor = new(
            repository,
            new RecordingUnitOfWorkFactory(),
            NoOpOperationalEventWriter.Instance,
            NoOpIdempotentAuditAppender.Instance);

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
                () => processor.ProcessAsync(
                    new ScriptedJobLock(true, true, true, true),
                    pageSize: 2,
                    TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The reservation expiry selector did not return a strict keyset page.",
            exception.Message);
        Assert.Equal(2, repository.ExpiryCalls);
    }

    private static QuotaExpiryCandidate Candidate(
        int suffix,
        DateTimeOffset leaseExpiresAt) => new(
            Id(100 + suffix),
            Id(200 + suffix),
            Id(300 + suffix),
            Id(400 + suffix),
            leaseExpiresAt);

    private static EntityId Id(int suffix) => new(
        Guid.Parse($"018f3a4b-5c6d-7e8f-9123-{suffix:D12}"));

    private static AttemptSettlementFact ConservativeFact(QuotaExpiryCandidate candidate) => new(
        candidate.AttemptId,
        Id(501),
        AttemptIndex: 1,
        candidate.ReservationId,
        candidate.GroupId,
        candidate.PeriodId,
        Id(502),
        Id(503),
        SettlementProvider.OpenAi,
        RequestedModel: "gpt-5-mini",
        UpstreamModel: "gpt-5-mini",
        UsageAttemptOutcome.Failed,
        UpstreamHttpStatus: null,
        ErrorCode: "reservation_lease_expired_after_dispatch",
        IsStreaming: false,
        Usage: new AttemptUsage(
            new TokenUsage(60, 40, 0, 0, 0),
            SettlementUsageSource.ConservativeEstimate,
            IsEstimated: true),
        Adjustment: null,
        DispatchStartedAt: DueAt.AddMinutes(-1),
        FirstTokenAt: null,
        CompletedAt: DueAt);

    private sealed class ScriptedQuotaLedgerRepository(
        IEnumerable<IReadOnlyList<QuotaExpiryCandidate>?> pages,
        IEnumerable<QuotaLedgerFailure> expiryResults,
        IEnumerable<AttemptSettlementFact?>? settlementFacts = null) : IQuotaLedgerRepository
    {
        private readonly Queue<IReadOnlyList<QuotaExpiryCandidate>?> _pages = new(pages);
        private readonly Queue<QuotaLedgerFailure> _expiryResults = new(expiryResults);
        private readonly Queue<AttemptSettlementFact?> _settlementFacts = new(
            settlementFacts ?? []);

        internal int PageCalls { get; private set; }

        internal int ExpiryCalls { get; private set; }

        internal List<QuotaExpiryCandidateKey?> Cursors { get; } = [];

        internal List<IUnitOfWorkContext> PageContexts { get; } = [];

        internal List<IUnitOfWorkContext> ExpiryContexts { get; } = [];

        internal List<ExpireReservationWrite> ExpiryWrites { get; } = [];

        public ValueTask<IReadOnlyList<QuotaExpiryCandidate>> ListDueExpiryCandidatesAsync(
            QuotaExpiryCandidateKey? after,
            int pageSize,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageCalls++;
            Cursors.Add(after);
            PageContexts.Add(unitOfWorkContext);
            IReadOnlyList<QuotaExpiryCandidate>? page = _pages.Count == 0
                ? []
                : _pages.Dequeue();
            return ValueTask.FromResult(page!);
        }

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ExpireAsync(
            ExpireReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpiryCalls++;
            ExpiryWrites.Add(write);
            ExpiryContexts.Add(unitOfWorkContext);
            QuotaLedgerFailure failure = _expiryResults.Dequeue();
            return ValueTask.FromResult(failure == QuotaLedgerFailure.None
                ? QuotaRepositoryResult<QuotaTransitionRow>.Success(new(
                    write.Candidate.ReservationId,
                    write.Candidate.PeriodId,
                    ReservationStatus.Expired,
                    TotalTokens: 1_000,
                    ConsumedTokens: 100,
                    ReservedTokens: 0,
                    RemainingTokens: 900))
                : QuotaRepositoryResult<QuotaTransitionRow>.Failed(failure));
        }

        public ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
            ReserveQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(ReserveAsync));

        public ValueTask<QuotaRepositoryResult<QuotaDispatchRow>> MarkDispatchedAsync(
            MarkReservationDispatchedWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(MarkDispatchedAsync));

        public ValueTask<QuotaRepositoryResult<QuotaRenewalRow>> RenewAsync(
            RenewReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(RenewAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
            SettleReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(SettleAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
            ReleaseReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(ReleaseAsync));

        public ValueTask<QuotaRepositoryResult<UsageAdjustmentRow>> AdjustUsageAsync(
            AdjustAttemptUsageWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(AdjustUsageAsync));

        public ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
            EntityId attemptId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                _settlementFacts.Count == 0 ? null : _settlementFacts.Dequeue());
        }

        private static InvalidOperationException Unexpected(string operation) => new(
            $"The {operation} repository method should not be called by the sweeper.");
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(
                this,
                new UnitOfWorkContext(BeginCalls)));
        }

        private sealed class UnitOfWork(
            RecordingUnitOfWorkFactory owner,
            IUnitOfWorkContext context) : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = context;

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }

        private sealed record UnitOfWorkContext(int Sequence) : IUnitOfWorkContext;
    }

    private sealed class ScriptedJobLock(params bool[] ownership) : IWorkerSessionLock
    {
        private readonly Queue<bool> _ownership = new(ownership);

        public WorkerJobIdentity Job { get; } = new("group-quota-reservation-sweeper");

        public long LockId => WorkerSessionLockId.Derive(Job);

        internal int VerificationCalls { get; private set; }

        public ValueTask<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerificationCalls++;
            return ValueTask.FromResult(_ownership.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingOperationalEventWriter(Action beforeWrite)
        : IOperationalEventWriter
    {
        internal int WriteCount { get; private set; }

        internal string? EventName { get; private set; }

        internal JsonElement Payload { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeWrite();
            WriteCount++;
            EventName = eventName;
            Payload = payload;
            CancellationToken = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpOperationalEventWriter : IOperationalEventWriter
    {
        internal static NoOpOperationalEventWriter Instance { get; } = new();

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingIdempotentAuditAppender(bool throwOnAppend = false)
        : IIdempotentAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        internal List<IUnitOfWorkContext> Contexts { get; } = [];

        public ValueTask AppendOnceAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwOnAppend)
            {
                throw new InvalidOperationException("Injected audit failure.");
            }

            Entries.Add(entry);
            Contexts.Add(unitOfWorkContext);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpIdempotentAuditAppender : IIdempotentAuditAppender
    {
        internal static NoOpIdempotentAuditAppender Instance { get; } = new();

        public ValueTask AppendOnceAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore MA0051
