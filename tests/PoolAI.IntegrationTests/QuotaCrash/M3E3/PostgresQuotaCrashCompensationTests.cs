// M3-E3 reservation-lifecycle partial; the filename intentionally matches the xUnit class.
#pragma warning disable MA0051 // Keep each PostgreSQL lifecycle evidence chain visible.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Workers;
using PoolAI.Modules.GroupQuota.Worker;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresQuotaCrashCompensationTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NonStreamingReservationRenewsAndStopsAtAbsoluteDeadline()
    {
        // Governing contracts: DEC-018 and AC-036. PostgreSQL owns both the
        // five-minute renewal duration and the ten-minute absolute deadline.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        DateTimeOffset reserveBefore = await ReadM3E3DatabaseClockAsync(cancellationToken)
            .ConfigureAwait(true);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(Command(scenario, scenario.PreDispatch), cancellationToken)
            .ConfigureAwait(true);
        DateTimeOffset reserveAfter = await ReadM3E3DatabaseClockAsync(cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserved);
        Assert.InRange(
            reserved.Value.Reservation.LeaseExpiresAt,
            reserveBefore.AddSeconds(295),
            reserveAfter.AddSeconds(305));
        Assert.InRange(
            reserved.Value.Reservation.MaxExpiresAt,
            reserveBefore.AddSeconds(595),
            reserveAfter.AddSeconds(605));

        DateTimeOffset renewBefore = await ReadM3E3DatabaseClockAsync(cancellationToken)
            .ConfigureAwait(true);
        RenewReservationCommand renewCommand = new(
            reserved.Value.Reservation,
            RenewalSequence: 1);
        Result<ReservationHandle> renewed = await ledger
            .RenewAsync(renewCommand, cancellationToken)
            .ConfigureAwait(true);
        DateTimeOffset renewAfter = await ReadM3E3DatabaseClockAsync(cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(renewed);
        Assert.Equal(reserved.Value.Reservation.MaxExpiresAt, renewed.Value.MaxExpiresAt);
        Assert.InRange(
            renewed.Value.LeaseExpiresAt,
            renewBefore.AddSeconds(295),
            renewAfter.AddSeconds(305));
        Assert.True(renewed.Value.LeaseExpiresAt <= renewed.Value.MaxExpiresAt);

        Result<ReservationHandle> replay = await ledger
            .RenewAsync(renewCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(replay);
        Assert.Equal(renewed.Value, replay.Value);

        await MoveM3E3ReservationToAbsoluteDeadlineAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Result<ReservationHandle> afterAbsoluteDeadline = await ledger
            .RenewAsync(
                new RenewReservationCommand(renewed.Value, RenewalSequence: 2),
                cancellationToken)
            .ConfigureAwait(true);
        AssertFailure(
            afterAbsoluteDeadline,
            "reservation_lease_lost",
            retryAfterSeconds: 1);

        // Leave no globally due reservation behind for the bounded sweeper
        // scenario. PostgreSQL integration tests intentionally share one
        // runtime, and xUnit does not guarantee method order within the class.
        await ExpireAsWorkerAsync(
            scenario,
            scenario.PreDispatch,
            expectedConsumedTokens: "0",
            expectedReservedTokens: "0",
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LateUsageAppendsOneAdjustmentToConservativeExpiry()
    {
        // Governing contracts: AC-019/039. A dispatched crash is conservatively
        // charged once; an authoritative late usage replay appends one immutable
        // adjustment instead of rewriting the original terminal attempt fact.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        await ExpireAsWorkerAsync(
            scenario,
            scenario.Dispatched,
            expectedConsumedTokens: "200",
            expectedReservedTokens: "0",
            cancellationToken).ConfigureAwait(true);

        CrashState conservative = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertConservativeCompensation(
            conservative,
            scenario.Dispatched,
            dispatchStartedAt);

        AdjustAttemptUsageCommand command = new(
            scenario.GroupId.AsEntityId(),
            scenario.Dispatched.AttemptId.AsEntityId(),
            scenario.AccountId.AsEntityId(),
            scenario.ChannelId.AsEntityId(),
            SettlementProvider.OpenAi,
            Model,
            UsageAttemptOutcome.Failed,
            UpstreamHttpStatus: null,
            ErrorCode: ConservativeError,
            UpstreamRequestId: null,
            DispatchStartedAt: dispatchStartedAt,
            FirstTokenAt: null,
            CompletedAt: Assert.IsType<DateTimeOffset>(conservative.AttemptCompletedAt),
            RequestOutcome: UsageRequestOutcome.Failed,
            CorrectedUsage: new TokenUsage(100, 55, 0, 0, 0),
            UsageSource: SettlementUsageSource.Upstream,
            RawUpstreamUsage: JsonSerializer.SerializeToElement(
                new { input_tokens = 100, output_tokens = 55 }),
            Reason: "late upstream usage");
        IUsageAdjustmentWriter writer = _fixture.WorkerServices
            .GetRequiredService<IUsageAdjustmentWriter>();

        Result<UsageAdjustmentResult> adjusted = await writer
            .AdjustAsync(command, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(adjusted);
        Assert.Equal(200, adjusted.Value.PreviousTokens);
        Assert.Equal(155, adjusted.Value.CorrectedTokens);
        Assert.Equal(-45, adjusted.Value.DeltaTokens);

        Result<UsageAdjustmentResult> replay = await writer
            .AdjustAsync(command, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(replay);
        Assert.Equal(adjusted.Value, replay.Value);

        CrashState afterReplay = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertLateAdjustment(afterReplay, conservative);
        M3E3AdjustmentEvidence evidence = await ReadM3E3AdjustmentEvidenceAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, evidence.AdjustmentCount);
        Assert.Equal(1, evidence.AdjustmentEventCount);
        Assert.Equal(1, evidence.AdjustmentOutboxCount);
        Assert.Equal(
            1,
            await ReadAttemptAuditCountAsync(
                scenario.Dispatched.AttemptId,
                "group_quota.attempt_fact_usage_adjusted",
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReservationSweeperUsesWorkerRoleSessionLockAndBoundedKeysetPages()
    {
        // Governing contract: one Worker owns the PostgreSQL session lock and
        // drains due reservations through strict, bounded keyset pages.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.PreDispatch,
            expectedReservedTokens: "120",
            cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "320",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);

        ReservationSweeperProcessor processor = new(
            _fixture.WorkerServices.GetRequiredService<IQuotaLedgerRepository>(),
            WorkerFactory(),
            _fixture.WorkerServices.GetRequiredService<IOperationalEventWriter>(),
            _fixture.WorkerServices.GetRequiredService<IIdempotentAuditAppender>());
        IWorkerSessionLockProvider lockProvider = _fixture.WorkerServices
            .GetRequiredService<IWorkerSessionLockProvider>();
        IWorkerSessionLock? jobLock = await lockProvider.TryAcquireAsync(
            WorkerJobs.ReservationSweeper,
            cancellationToken).ConfigureAwait(true);
        Assert.NotNull(jobLock);

        ReservationSweepProcessResult result;
        await using (jobLock.ConfigureAwait(false))
        {
            result = await processor.ProcessAsync(
                jobLock,
                pageSize: 1,
                cancellationToken).ConfigureAwait(true);
        }

        Assert.Equal(ReservationSweepProcessDisposition.Completed, result.Disposition);
        // PostgreSQL integration tests share one runtime and xUnit does not
        // guarantee class order. Other reconciliation tests may leave valid
        // globally due reservations for this global Worker to drain. With a
        // page size of one, a completed strict keyset scan must read exactly
        // one page per candidate plus the final empty page.
        Assert.True(result.ScannedCount >= 2);
        Assert.Equal(result.ScannedCount + 1, result.PageCount);
        Assert.Equal(result.ScannedCount, result.ExpiredCount);
        Assert.Equal(0, result.RaceLostCount);
        Assert.Equal(TimeSpan.FromSeconds(30), ReservationSweeperService.SweepInterval);
        Assert.True(
            ReservationSweeperService.RoundBudget
            < ReservationSweeperService.SweepInterval);

        CrashState preDispatch = await ReadCrashStateAsync(
            scenario.PreDispatch,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(scenario.PreDispatch.ReservationId, preDispatch.ReservationId);
        Assert.Equal("expired", preDispatch.ReservationStatus);
        Assert.Null(preDispatch.DispatchStartedAt);
        Assert.Null(preDispatch.ActualTokens);
        Assert.Null(preDispatch.AttemptId);
        Assert.Equal("0", preDispatch.ExpiryDeltaConsumed);
        Assert.Equal("-120", preDispatch.ExpiryDeltaReserved);
        Assert.Equal("false", preDispatch.ConservativeExpiry);
        CrashState conservative = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertConservativeCompensation(
            conservative,
            scenario.Dispatched,
            dispatchStartedAt);
        Assert.Equal(
            0,
            await ReadAttemptAuditCountAsync(
                scenario.PreDispatch.AttemptId,
                "group_quota.attempt_fact_conservative_expired",
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            1,
            await ReadAttemptAuditCountAsync(
                scenario.Dispatched.AttemptId,
                "group_quota.attempt_fact_conservative_expired",
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LateSettlementRemainsOnTheOriginalQuotaPeriodAfterReset()
    {
        // Governing contract: AC-017 and docs/database/README.md section 11.4.
        // A reset selects a new current period but cannot retarget an existing
        // reservation or its append-only late-usage correction.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        await ExpireAsWorkerAsync(
            scenario,
            scenario.Dispatched,
            expectedConsumedTokens: "200",
            expectedReservedTokens: "0",
            cancellationToken).ConfigureAwait(true);

        CrashState conservative = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertConservativeCompensation(
            conservative,
            scenario.Dispatched,
            dispatchStartedAt);
        M3E3PeriodEvidence beforeReset = await ReadM3E3PeriodEvidenceAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(scenario.PeriodId, beforeReset.CurrentPeriodId);
        Assert.Equal(scenario.PeriodId, beforeReset.ReservationPeriodId);
        Assert.Equal("200", beforeReset.OriginalPeriodConsumedTokens);
        Assert.Equal("0", beforeReset.OriginalPeriodReservedTokens);
        Assert.Null(beforeReset.OriginalPeriodClosedAt);

        Guid nextPeriodId = Guid.CreateVersion7();
        await ResetM3E3QuotaPeriodAsync(
            scenario,
            nextPeriodId,
            newTotalTokens: 777,
            cancellationToken).ConfigureAwait(true);
        M3E3PeriodEvidence afterReset = await ReadM3E3PeriodEvidenceAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(nextPeriodId, afterReset.CurrentPeriodId);
        Assert.Equal("0", afterReset.CurrentPeriodConsumedTokens);
        Assert.Equal("0", afterReset.CurrentPeriodReservedTokens);
        Assert.Equal(scenario.PeriodId, afterReset.ReservationPeriodId);
        Assert.Equal("200", afterReset.OriginalPeriodConsumedTokens);
        Assert.Equal("0", afterReset.OriginalPeriodReservedTokens);
        Assert.NotNull(afterReset.OriginalPeriodClosedAt);
        Assert.Equal(beforeReset.TerminalFactFingerprint, afterReset.TerminalFactFingerprint);

        await AdjustLateUsageAsWorkerAsync(
            scenario,
            conservative,
            cancellationToken).ConfigureAwait(true);

        CrashState adjusted = await ReadCrashStateAsync(
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        AssertLateAdjustment(adjusted, conservative);
        M3E3PeriodEvidence afterAdjustment = await ReadM3E3PeriodEvidenceAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(nextPeriodId, afterAdjustment.CurrentPeriodId);
        Assert.Equal("0", afterAdjustment.CurrentPeriodConsumedTokens);
        Assert.Equal("0", afterAdjustment.CurrentPeriodReservedTokens);
        Assert.Equal(scenario.PeriodId, afterAdjustment.ReservationPeriodId);
        Assert.Equal("155", afterAdjustment.OriginalPeriodConsumedTokens);
        Assert.Equal("0", afterAdjustment.OriginalPeriodReservedTokens);
        Assert.Equal(
            afterReset.OriginalPeriodClosedAt,
            afterAdjustment.OriginalPeriodClosedAt);
        Assert.Equal(
            afterReset.CurrentPeriodFingerprint,
            afterAdjustment.CurrentPeriodFingerprint);
        Assert.Equal(
            beforeReset.TerminalFactFingerprint,
            afterAdjustment.TerminalFactFingerprint);

        M3E3AdjustmentEvidence evidence = await ReadM3E3AdjustmentEvidenceAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, evidence.AdjustmentCount);
        Assert.Equal(1, evidence.AdjustmentEventCount);
        Assert.Equal(1, evidence.AdjustmentOutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SettlementCommitMakesBlockedExpiryLoseWithStableClassification()
    {
        // Governing contracts: DEC-039 and AC-039. Settle and expiry share the
        // quota row linearization lock; a committed settlement leaves exactly
        // one terminal fact and classifies the blocked sweeper write as a race.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        QuotaExpiryCandidate candidate = await ReadM3E3ExpiryCandidateAsync(
            scenario,
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);

        IUnitOfWork settlementUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable settlementLease =
            settlementUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession settlementSession = PostgresUnitOfWorkAccessor.Require(
            settlementUnitOfWork.Context);
        int settlementPid = await ReadBackendPidAsync(
            settlementSession,
            cancellationToken).ConfigureAwait(true);
        await CallSettleAsync(
            settlementSession,
            scenario,
            Provider,
            dispatchStartedAt,
            cancellationToken).ConfigureAwait(true);

        IUnitOfWork expiryUnitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable expiryLease =
            expiryUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession expirySession = PostgresUnitOfWorkAccessor.Require(
            expiryUnitOfWork.Context);
        int expiryPid = await ReadBackendPidAsync(expirySession, cancellationToken)
            .ConfigureAwait(true);
        IQuotaLedgerRepository expiryRepository = _fixture.WorkerServices
            .GetRequiredService<IQuotaLedgerRepository>();
        Task<QuotaRepositoryResult<QuotaTransitionRow>> expiryTask = expiryRepository
            .ExpireAsync(
                CreateM3E3ExpiryWrite(candidate, "settlement-wins"),
                expiryUnitOfWork.Context,
                cancellationToken)
            .AsTask();

        Assert.True(
            await WaitForBackendLockAsync(expiryPid, settlementPid, cancellationToken)
                .ConfigureAwait(true),
            "Expiry did not wait for the settlement quota-row lock.");
        await settlementUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        QuotaRepositoryResult<QuotaTransitionRow> expiry = await expiryTask
            .ConfigureAwait(true);
        Assert.False(expiry.IsSuccess);
        Assert.Equal(QuotaLedgerFailure.ReservationExpiryRaceLost, expiry.Failure);

        M3E3RaceEvidence evidence = await ReadM3E3RaceEvidenceAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("settled", evidence.ReservationStatus);
        Assert.Equal("200", evidence.ConsumedTokens);
        Assert.Equal("0", evidence.ReservedTokens);
        Assert.Equal(1, evidence.UsageAttemptCount);
        Assert.Equal(1, evidence.SettledEventCount);
        Assert.Equal(1, evidence.SettledOutboxCount);
        Assert.Equal(0, evidence.ExpiredEventCount);
        Assert.Equal(0, evidence.ExpiredOutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpiryCommitRedirectsBlockedSettlementToAppendOnlyAdjustment()
    {
        // The opposite commit order has a different contractual loser result:
        // a dispatched expiry is immutable, so late reliable usage must append
        // an adjustment instead of creating a competing settlement terminal.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        await ReserveAttemptAsync(
            scenario,
            scenario.Dispatched,
            expectedReservedTokens: "200",
            cancellationToken).ConfigureAwait(true);
        DateTimeOffset dispatchStartedAt = await MarkDispatchedAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        await ForceM3E3ReservationDueAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        QuotaExpiryCandidate candidate = await ReadM3E3ExpiryCandidateAsync(
            scenario,
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);

        IUnitOfWork expiryUnitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable expiryLease =
            expiryUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession expirySession = PostgresUnitOfWorkAccessor.Require(
            expiryUnitOfWork.Context);
        int expiryPid = await ReadBackendPidAsync(expirySession, cancellationToken)
            .ConfigureAwait(true);
        IQuotaLedgerRepository expiryRepository = _fixture.WorkerServices
            .GetRequiredService<IQuotaLedgerRepository>();
        QuotaRepositoryResult<QuotaTransitionRow> expiry = await expiryRepository
            .ExpireAsync(
                CreateM3E3ExpiryWrite(candidate, "expiry-wins-settlement"),
                expiryUnitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(true);
        Assert.True(expiry.IsSuccess);

        IUnitOfWork settlementUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable settlementLease =
            settlementUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession settlementSession = PostgresUnitOfWorkAccessor.Require(
            settlementUnitOfWork.Context);
        int settlementPid = await ReadBackendPidAsync(
            settlementSession,
            cancellationToken).ConfigureAwait(true);
        Task settlementTask = CallSettleAsync(
            settlementSession,
            scenario,
            Provider,
            dispatchStartedAt,
            cancellationToken).AsTask();

        Assert.True(
            await WaitForBackendLockAsync(settlementPid, expiryPid, cancellationToken)
                .ConfigureAwait(true),
            "Settlement did not wait for the expiry quota-row lock.");
        await expiryUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        PostgresException settlementFailure = await Assert.ThrowsAsync<PostgresException>(
            () => settlementTask).ConfigureAwait(true);
        Assert.Equal(PostgresErrorCodes.RaiseException, settlementFailure.SqlState);
        Assert.Equal(
            "reservation_terminal_use_adjust_usage",
            settlementFailure.MessageText);

        M3E3RaceEvidence evidence = await ReadM3E3RaceEvidenceAsync(
            scenario.Dispatched.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("expired", evidence.ReservationStatus);
        Assert.Equal("200", evidence.ConsumedTokens);
        Assert.Equal("0", evidence.ReservedTokens);
        Assert.Equal(1, evidence.UsageAttemptCount);
        Assert.Equal(0, evidence.SettledEventCount);
        Assert.Equal(0, evidence.SettledOutboxCount);
        Assert.Equal(1, evidence.ExpiredEventCount);
        Assert.Equal(1, evidence.ExpiredOutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RenewalCommitMakesBlockedExpiryLoseThenAllowsOneLaterExpiry()
    {
        // A renewal samples the database clock while holding the quota row. A
        // sweeper candidate visible from the pre-commit MVCC version must lose
        // after the renewed lease commits and may be recovered exactly once later.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        Result<ReserveQuotaResult> reserved = await Ledger()
            .ReserveAsync(Command(scenario, scenario.PreDispatch), cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserved);

        IUnitOfWork renewalUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable renewalLease =
            renewalUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession renewalSession = PostgresUnitOfWorkAccessor.Require(
            renewalUnitOfWork.Context);
        int renewalPid = await ReadBackendPidAsync(renewalSession, cancellationToken)
            .ConfigureAwait(true);
        DateTimeOffset oldExpiry = await ShortenM3E3LeaseAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        IQuotaLedgerRepository renewalRepository = _fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        RenewReservationWrite renewalWrite = new(
            new RenewReservationCommand(reserved.Value.Reservation, RenewalSequence: 1),
            QuotaMutationIdentityFactory.ForRenewal(
                scenario.PreDispatch.AttemptId.AsEntityId(),
                renewalSequence: 1));
        QuotaRepositoryResult<QuotaRenewalRow> renewal = await renewalRepository
            .RenewAsync(
                renewalWrite,
                renewalUnitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(true);
        Assert.True(renewal.IsSuccess);
        Assert.True(renewal.Value!.LeaseExpiresAt > oldExpiry);

        await WaitUntilM3E3DatabaseClockPassesAsync(oldExpiry, cancellationToken)
            .ConfigureAwait(true);
        QuotaExpiryCandidate candidate = await ReadM3E3ExpiryCandidateAsync(
            scenario,
            scenario.PreDispatch,
            cancellationToken).ConfigureAwait(true);

        IUnitOfWork expiryUnitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable expiryLease =
            expiryUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession expirySession = PostgresUnitOfWorkAccessor.Require(
            expiryUnitOfWork.Context);
        int expiryPid = await ReadBackendPidAsync(expirySession, cancellationToken)
            .ConfigureAwait(true);
        IQuotaLedgerRepository expiryRepository = _fixture.WorkerServices
            .GetRequiredService<IQuotaLedgerRepository>();
        Task<QuotaRepositoryResult<QuotaTransitionRow>> expiryTask = expiryRepository
            .ExpireAsync(
                CreateM3E3ExpiryWrite(candidate, "renewal-wins"),
                expiryUnitOfWork.Context,
                cancellationToken)
            .AsTask();

        Assert.True(
            await WaitForBackendLockAsync(expiryPid, renewalPid, cancellationToken)
                .ConfigureAwait(true),
            "Expiry did not wait for the renewal quota-row lock.");
        await renewalUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        QuotaRepositoryResult<QuotaTransitionRow> racedExpiry = await expiryTask
            .ConfigureAwait(true);
        Assert.False(racedExpiry.IsSuccess);
        Assert.Equal(QuotaLedgerFailure.ReservationExpiryRaceLost, racedExpiry.Failure);

        await ForceM3E3ReservationDueAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        await ExpireAsWorkerAsync(
            scenario,
            scenario.PreDispatch,
            expectedConsumedTokens: "0",
            expectedReservedTokens: "0",
            cancellationToken).ConfigureAwait(true);

        M3E3RaceEvidence evidence = await ReadM3E3RaceEvidenceAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("expired", evidence.ReservationStatus);
        Assert.Equal("0", evidence.ConsumedTokens);
        Assert.Equal("0", evidence.ReservedTokens);
        Assert.Equal(0, evidence.UsageAttemptCount);
        Assert.Equal(1, evidence.RenewedEventCount);
        Assert.Equal(1, evidence.RenewedOutboxCount);
        Assert.Equal(1, evidence.ExpiredEventCount);
        Assert.Equal(1, evidence.ExpiredOutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExpiryCommitMakesBlockedRenewalLoseWithStableClassification()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        Result<ReserveQuotaResult> reserved = await Ledger()
            .ReserveAsync(Command(scenario, scenario.PreDispatch), cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserved);
        await ForceM3E3ReservationDueAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        QuotaExpiryCandidate candidate = await ReadM3E3ExpiryCandidateAsync(
            scenario,
            scenario.PreDispatch,
            cancellationToken).ConfigureAwait(true);

        IUnitOfWork expiryUnitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable expiryLease =
            expiryUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession expirySession = PostgresUnitOfWorkAccessor.Require(
            expiryUnitOfWork.Context);
        int expiryPid = await ReadBackendPidAsync(expirySession, cancellationToken)
            .ConfigureAwait(true);
        IQuotaLedgerRepository expiryRepository = _fixture.WorkerServices
            .GetRequiredService<IQuotaLedgerRepository>();
        QuotaRepositoryResult<QuotaTransitionRow> expiry = await expiryRepository
            .ExpireAsync(
                CreateM3E3ExpiryWrite(candidate, "expiry-wins-renewal"),
                expiryUnitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(true);
        Assert.True(expiry.IsSuccess);

        IUnitOfWork renewalUnitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(true);
        await using ConfiguredAsyncDisposable renewalLease =
            renewalUnitOfWork.ConfigureAwait(true);
        PostgresTransactionSession renewalSession = PostgresUnitOfWorkAccessor.Require(
            renewalUnitOfWork.Context);
        int renewalPid = await ReadBackendPidAsync(renewalSession, cancellationToken)
            .ConfigureAwait(true);
        IQuotaLedgerRepository renewalRepository = _fixture.ApiServices
            .GetRequiredService<IQuotaLedgerRepository>();
        RenewReservationWrite renewalWrite = new(
            new RenewReservationCommand(reserved.Value.Reservation, RenewalSequence: 1),
            QuotaMutationIdentityFactory.ForRenewal(
                scenario.PreDispatch.AttemptId.AsEntityId(),
                renewalSequence: 1));
        Task<QuotaRepositoryResult<QuotaRenewalRow>> renewalTask = renewalRepository
            .RenewAsync(
                renewalWrite,
                renewalUnitOfWork.Context,
                cancellationToken)
            .AsTask();

        Assert.True(
            await WaitForBackendLockAsync(renewalPid, expiryPid, cancellationToken)
                .ConfigureAwait(true),
            "Renewal did not wait for the expiry quota-row lock.");
        await expiryUnitOfWork.CommitAsync(cancellationToken).ConfigureAwait(true);
        QuotaRepositoryResult<QuotaRenewalRow> renewal = await renewalTask
            .ConfigureAwait(true);
        Assert.False(renewal.IsSuccess);
        Assert.Equal(QuotaLedgerFailure.ReservationLeaseLost, renewal.Failure);

        M3E3RaceEvidence evidence = await ReadM3E3RaceEvidenceAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("expired", evidence.ReservationStatus);
        Assert.Equal("0", evidence.ConsumedTokens);
        Assert.Equal("0", evidence.ReservedTokens);
        Assert.Equal(0, evidence.UsageAttemptCount);
        Assert.Equal(0, evidence.RenewedEventCount);
        Assert.Equal(0, evidence.RenewedOutboxCount);
        Assert.Equal(1, evidence.ExpiredEventCount);
        Assert.Equal(1, evidence.ExpiredOutboxCount);
    }

    private static ExpireReservationWrite CreateM3E3ExpiryWrite(
        QuotaExpiryCandidate candidate,
        string stage)
    {
        MutationIds mutation = MutationIds.Create($"m3e3-race-expire-{stage}");
        return new ExpireReservationWrite(
            candidate,
            new QuotaMutationIdentity(
                mutation.EventId.AsEntityId(),
                mutation.OutboxId.AsEntityId(),
                mutation.IdempotencyKey),
            "M3-E3 deterministic reservation race");
    }

    private async ValueTask<QuotaExpiryCandidate> ReadM3E3ExpiryCandidateAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT reservation.id,
                   reservation.attempt_id,
                   reservation.group_id,
                   reservation.period_id,
                   reservation.lease_expires_at,
                   reservation.lease_expires_at <= clock_timestamp()
            FROM public.group_token_reservations AS reservation
            WHERE reservation.group_id = $1
              AND reservation.attempt_id = $2
              AND reservation.status = 'pending';
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        QuotaExpiryCandidate candidate = new(
            reader.GetGuid(0).AsEntityId(),
            reader.GetGuid(1).AsEntityId(),
            reader.GetGuid(2).AsEntityId(),
            reader.GetGuid(3).AsEntityId(),
            reader.GetFieldValue<DateTimeOffset>(4));
        Assert.True(reader.GetBoolean(5), "The deterministic expiry candidate was not due.");
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return candidate;
    }

    private async ValueTask<DateTimeOffset> ShortenM3E3LeaseAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            WITH boundary AS MATERIALIZED (
                SELECT clock_timestamp() + interval '2 seconds' AS lease_expires_at
            )
            UPDATE public.group_token_reservations AS reservation
            SET lease_expires_at = boundary.lease_expires_at,
                updated_at = clock_timestamp()
            FROM boundary
            WHERE reservation.attempt_id = $1
              AND reservation.status = 'pending'
            RETURNING reservation.lease_expires_at;
            """);
        command.Parameters.AddWithValue(attemptId);
        DateTime timestamp = Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        return new DateTimeOffset(timestamp);
    }

    private async ValueTask WaitUntilM3E3DatabaseClockPassesAsync(
        DateTimeOffset boundary,
        CancellationToken cancellationToken)
    {
        for (int probe = 0; probe < 250; probe++)
        {
            if (await ReadM3E3DatabaseClockAsync(cancellationToken).ConfigureAwait(false)
                >= boundary)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException("The PostgreSQL clock did not pass the race boundary.");
    }

    private async ValueTask<M3E3RaceEvidence> ReadM3E3RaceEvidenceAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT reservation.status,
                   period.consumed_tokens::text,
                   period.reserved_tokens::text,
                   (SELECT count(*) FROM public.usage_attempts AS attempt
                    WHERE attempt.attempt_id = reservation.attempt_id),
                   (SELECT count(*) FROM public.group_quota_events AS event
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'renewed'),
                   (SELECT count(*)
                    FROM public.group_quota_events AS event
                    JOIN public.outbox_messages AS message
                      ON message.source_event_sequence = event.event_sequence
                     AND message.payload ->> 'event_id' = event.id::text
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'renewed'),
                   (SELECT count(*) FROM public.group_quota_events AS event
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'settled'),
                   (SELECT count(*)
                    FROM public.group_quota_events AS event
                    JOIN public.outbox_messages AS message
                      ON message.source_event_sequence = event.event_sequence
                     AND message.payload ->> 'event_id' = event.id::text
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'settled'),
                   (SELECT count(*) FROM public.group_quota_events AS event
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'expired'),
                   (SELECT count(*)
                    FROM public.group_quota_events AS event
                    JOIN public.outbox_messages AS message
                      ON message.source_event_sequence = event.event_sequence
                     AND message.payload ->> 'event_id' = event.id::text
                    WHERE event.attempt_id = reservation.attempt_id
                      AND event.event_type = 'expired')
            FROM public.group_token_reservations AS reservation
            JOIN public.group_quota_periods AS period
              ON period.id = reservation.period_id
            WHERE reservation.attempt_id = $1;
            """);
        command.Parameters.AddWithValue(attemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E3RaceEvidence evidence = new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private async ValueTask<DateTimeOffset> ReadM3E3DatabaseClockAsync(
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand(
            "SELECT clock_timestamp();");
        DateTime timestamp = Assert.IsType<DateTime>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        return new DateTimeOffset(timestamp);
    }

    private async ValueTask MoveM3E3ReservationToAbsoluteDeadlineAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            WITH boundary AS MATERIALIZED (
                SELECT clock_timestamp() AS sampled_at
            )
            UPDATE public.group_token_reservations AS reservation
            SET lease_expires_at = boundary.sampled_at,
                max_expires_at = boundary.sampled_at,
                updated_at = boundary.sampled_at
            FROM boundary
            WHERE reservation.attempt_id = $1
              AND reservation.status = 'pending';
            """);
        command.Parameters.AddWithValue(attemptId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ForceM3E3ReservationDueAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            UPDATE public.group_token_reservations
            SET lease_expires_at = created_at + interval '1 microsecond',
                updated_at = clock_timestamp()
            WHERE attempt_id = $1 AND status = 'pending';
            """);
        command.Parameters.AddWithValue(attemptId);
        Assert.Equal(
            1,
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask ResetM3E3QuotaPeriodAsync(
        CrashScenario scenario,
        Guid newPeriodId,
        int newTotalTokens,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWork.Context);
        await AssertCurrentRoleAsync(session, "poolai_api", cancellationToken)
            .ConfigureAwait(false);

        long expectedVersion;
        using (NpgsqlCommand version = session.CreateCommand("""
                   SELECT version
                   FROM public.group_token_quotas
                   WHERE group_id = $1;
                   """))
        {
            version.Parameters.AddWithValue(scenario.GroupId);
            expectedVersion = Assert.IsType<long>(await version
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        MutationIds mutation = MutationIds.Create("m3e3-period-reset");
        using (NpgsqlCommand reset = session.CreateCommand("""
                   SELECT result_period_id, result_period_number,
                          result_total_tokens::text,
                          result_consumed_tokens::text,
                          result_reserved_tokens::text
                   FROM public.poolai_group_quota_reset(
                       $1, $2, $3, $4, $5, $6, $7, $8,
                       'AC-017 original-period late settlement');
                   """))
        {
            reset.Parameters.AddWithValue(scenario.GroupId);
            reset.Parameters.AddWithValue(newPeriodId);
            reset.Parameters.AddWithValue(newTotalTokens);
            reset.Parameters.AddWithValue(expectedVersion);
            reset.Parameters.AddWithValue(scenario.UserId);
            reset.Parameters.AddWithValue(mutation.EventId);
            reset.Parameters.AddWithValue(mutation.OutboxId);
            reset.Parameters.AddWithValue(mutation.IdempotencyKey);
            using NpgsqlDataReader reader = await reset
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
            Assert.Equal(newPeriodId, reader.GetGuid(0));
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(
                newTotalTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2));
            Assert.Equal("0", reader.GetString(3));
            Assert.Equal("0", reader.GetString(4));
            Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<M3E3PeriodEvidence> ReadM3E3PeriodEvidenceAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                quota.current_period_id,
                current_period.consumed_tokens::text,
                current_period.reserved_tokens::text,
                reservation.period_id,
                original_period.consumed_tokens::text,
                original_period.reserved_tokens::text,
                original_period.closed_at,
                pg_catalog.jsonb_build_object(
                    'quota_version', quota.version,
                    'period_id', current_period.id,
                    'period_total_tokens', current_period.total_tokens::text,
                    'period_consumed_tokens', current_period.consumed_tokens::text,
                    'period_reserved_tokens', current_period.reserved_tokens::text,
                    'period_version', current_period.version,
                    'period_opened_at', current_period.opened_at,
                    'period_updated_at', current_period.updated_at
                )::text,
                pg_catalog.jsonb_build_object(
                    'reservation_id', reservation.id,
                    'reservation_status', reservation.status,
                    'reservation_actual_tokens', reservation.actual_tokens::text,
                    'reservation_usage_source', reservation.usage_source,
                    'reservation_dispatch_started_at', reservation.dispatch_started_at,
                    'reservation_expired_at', reservation.expired_at,
                    'attempt_id', attempt.attempt_id,
                    'attempt_status', attempt.status,
                    'attempt_input_tokens', attempt.input_tokens::text,
                    'attempt_output_tokens', attempt.output_tokens::text,
                    'attempt_total_tokens', attempt.total_tokens::text,
                    'attempt_usage_source', attempt.usage_source,
                    'attempt_is_estimated', attempt.is_estimated,
                    'attempt_error_code', attempt.error_code,
                    'attempt_dispatch_started_at', attempt.dispatch_started_at,
                    'attempt_completed_at', attempt.completed_at,
                    'expiry_event_id', expiry_event.id,
                    'expiry_event_type', expiry_event.event_type,
                    'expiry_delta_consumed', expiry_event.delta_consumed_tokens::text,
                    'expiry_delta_reserved', expiry_event.delta_reserved_tokens::text,
                    'expiry_metadata', expiry_event.metadata
                )::text
            FROM public.group_token_quotas AS quota
            JOIN public.group_quota_periods AS current_period
              ON current_period.id = quota.current_period_id
            JOIN public.group_token_reservations AS reservation
              ON reservation.group_id = quota.group_id
             AND reservation.attempt_id = $2
            JOIN public.group_quota_periods AS original_period
              ON original_period.id = reservation.period_id
            JOIN public.usage_attempts AS attempt
              ON attempt.attempt_id = reservation.attempt_id
            JOIN public.group_quota_events AS expiry_event
              ON expiry_event.attempt_id = reservation.attempt_id
             AND expiry_event.event_type = 'expired'
            WHERE quota.group_id = $1;
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.Dispatched.AttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E3PeriodEvidence evidence = new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            NullableTimestamp(reader, 6),
            reader.GetString(7),
            reader.GetString(8));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private async ValueTask<M3E3AdjustmentEvidence> ReadM3E3AdjustmentEvidenceAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)
                 FROM public.usage_attempt_adjustments
                 WHERE attempt_id = $1),
                (SELECT count(*)
                 FROM public.group_quota_events
                 WHERE attempt_id = $1 AND event_type = 'usage_adjusted'),
                (SELECT count(*)
                 FROM public.outbox_messages AS outbox
                 JOIN public.group_quota_events AS quota_event
                   ON outbox.payload ->> 'event_id' = quota_event.id::text
                 WHERE quota_event.attempt_id = $1
                   AND quota_event.event_type = 'usage_adjusted');
            """);
        command.Parameters.AddWithValue(attemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E3AdjustmentEvidence evidence = new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private async ValueTask<long> ReadAttemptAuditCountAsync(
        Guid attemptId,
        string action,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT count(*)
            FROM public.audit_logs
            WHERE target_type = 'usage_attempt'
              AND target_id = $1
              AND action = $2;
            """);
        command.Parameters.AddWithValue(attemptId);
        command.Parameters.AddWithValue(action);
        return Assert.IsType<long>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private sealed record M3E3AdjustmentEvidence(
        long AdjustmentCount,
        long AdjustmentEventCount,
        long AdjustmentOutboxCount);

    private sealed record M3E3RaceEvidence(
        string ReservationStatus,
        string ConsumedTokens,
        string ReservedTokens,
        long UsageAttemptCount,
        long RenewedEventCount,
        long RenewedOutboxCount,
        long SettledEventCount,
        long SettledOutboxCount,
        long ExpiredEventCount,
        long ExpiredOutboxCount);

    private sealed record M3E3PeriodEvidence(
        Guid CurrentPeriodId,
        string CurrentPeriodConsumedTokens,
        string CurrentPeriodReservedTokens,
        Guid ReservationPeriodId,
        string OriginalPeriodConsumedTokens,
        string OriginalPeriodReservedTokens,
        DateTimeOffset? OriginalPeriodClosedAt,
        string CurrentPeriodFingerprint,
        string TerminalFactFingerprint);
}
#pragma warning restore MA0051
