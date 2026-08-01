// M3-E2 edge-case partial; the filename intentionally matches the xUnit class.
#pragma warning disable MA0051 // Keep each PostgreSQL overflow rollback evidence chain visible.
using System.Globalization;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresQuotaCrashCompensationTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SeededRandomReservationPressureNeverOversubscribesTheGroupPeriod()
    {
        // Governing contract: AC-011. Seeded estimates vary the lock acquisition
        // workload while assertions rely only on invariant outcomes, not scheduling.
        const int participantCount = 24;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create() with { TotalTokens = 3_000 };
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        Random random = new(0x0311_2026);
        ReserveQuotaCommand[] commands = Enumerable.Range(0, participantCount)
            .Select(_ => random.Next(100, 451))
            .Select(estimate => Command(
                scenario,
                NewAttempt(estimate, estimate / 2, estimate - estimate / 2)))
            .ToArray();
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<ReserveQuotaResult>>[] tasks = commands
            .Select(async command =>
            {
                await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await ledger.ReserveAsync(command, cancellationToken).ConfigureAwait(false);
            })
            .ToArray();

        barrier.SetResult();
        Result<ReserveQuotaResult>[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        ReserveQuotaResult[] accepted = results
            .Where(static result => result.IsSuccess)
            .Select(static result => result.Value)
            .ToArray();
        Result<ReserveQuotaResult>[] rejected = results
            .Where(static result => result.IsFailure)
            .ToArray();
        BigInteger acceptedTokens = accepted.Aggregate(
            BigInteger.Zero,
            static (sum, result) => sum + result.Reservation.EstimatedTokens);
        Assert.InRange(accepted.Length, 1, participantCount - 1);
        Assert.Equal(participantCount - accepted.Length, rejected.Length);
        Assert.All(rejected, result => AssertFailure(result, "group_quota_reserved", 1));
        Assert.InRange(acceptedTokens, BigInteger.One, new BigInteger(scenario.TotalTokens));
        ConcurrencyEvidence evidence = await ReadConcurrencyEvidenceAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(acceptedTokens.ToString(CultureInfo.InvariantCulture), evidence.ReservedTokens);
        Assert.Equal(accepted.Length, evidence.RequestCount);
        Assert.Equal(accepted.Length, evidence.ReservationCount);
        Assert.Equal(accepted.Length, evidence.ReservedEventCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EachUpstreamCallCreatesAndSettlesAnIndependentAttempt()
    {
        // DEC-015: two upstream calls share one request but own independent facts.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        ReserveQuotaCommand firstCommand = Command(scenario, scenario.PreDispatch);
        DispatchedReservationHandle firstDispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            firstCommand,
            cancellationToken).ConfigureAwait(true);
        SettleReservationCommand firstSettlement = new(
            firstDispatch,
            UsageAttemptOutcome.Failed,
            500,
            "upstream_error",
            null,
            null,
            firstDispatch.DispatchStartedAt.AddMilliseconds(1),
            RequestOutcome: null,
            Usage: new TokenUsage(6, 4, 0, 0, 0),
            UsageSource: SettlementUsageSource.Upstream,
            RawUpstreamUsage: null);
        AssertSuccess(await ledger.SettleAsync(firstSettlement, cancellationToken)
            .ConfigureAwait(true));

        CrashAttempt secondAttempt = NewAttempt(60, 40, 20);
        ReserveQuotaCommand secondCommand = Command(scenario, secondAttempt) with
        {
            RequestId = firstCommand.RequestId,
            AttemptIndex = 1,
        };
        DispatchedReservationHandle secondDispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            secondCommand,
            cancellationToken).ConfigureAwait(true);
        AssertSuccess(await ledger.SettleAsync(
            SuccessfulSettlement(
                secondDispatch,
                new TokenUsage(20, 10, 0, 0, 0),
                "upstream-request-second-attempt"),
            cancellationToken).ConfigureAwait(true));

        AttemptSettlementFact firstFact = await ReadFactAsync(
            firstCommand.AttemptId,
            cancellationToken).ConfigureAwait(true);
        AttemptSettlementFact secondFact = await ReadFactAsync(
            secondCommand.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(0, firstFact.AttemptIndex);
        Assert.Equal(1, secondFact.AttemptIndex);
        Assert.Equal(firstFact.RequestId, secondFact.RequestId);
        Assert.NotEqual(firstFact.ReservationId, secondFact.ReservationId);
        Assert.Equal(UsageAttemptOutcome.Failed, firstFact.Outcome);
        Assert.Equal(UsageAttemptOutcome.Succeeded, secondFact.Outcome);
        Assert.Equal(
            "40",
            await ReadPeriodConsumedAsync(
                scenario.PeriodId,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ImmutableUsageRequestIdentityConflictRollsBackQuotaWrites()
    {
        // Governing contracts: docs/database/README.md sections 5-6 and AC-014.
        // request_id is immutable across retries; a conflicting first-attempt
        // insert must fail closed without reserving quota or emitting an event.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        ReserveQuotaCommand original = Command(scenario, scenario.PreDispatch) with
        {
            ClientRequestId = "m3e2-immutable-request",
        };
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(original, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserved);
        M3E2EdgeFootprint before = await ReadM3E2EdgeFootprintAsync(
            scenario,
            original.RequestId.Value,
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);

        CrashAttempt conflictingAttempt = NewAttempt(60, 40, 20);
        ReserveQuotaCommand conflict = Command(scenario, conflictingAttempt) with
        {
            RequestId = original.RequestId,
            ClientRequestId = "m3e2-conflicting-request",
        };
        Result<ReserveQuotaResult> rejected = await ledger
            .ReserveAsync(conflict, cancellationToken)
            .ConfigureAwait(true);

        AssertFailure(rejected, "idempotency_conflict", retryAfterSeconds: null);
        Assert.Equal(
            before,
            await ReadM3E2EdgeFootprintAsync(
                scenario,
                original.RequestId.Value,
                scenario.PreDispatch.AttemptId,
                cancellationToken).ConfigureAwait(true));
        Assert.False(await ReservationExistsAsync(
            conflictingAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AttemptIndexGapRollsBackReservationEventAndOutbox()
    {
        // Governing contract: the database README section 5 retry identity.
        // Attempt indices for one request are a contiguous zero-based sequence.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        ReserveQuotaCommand firstCommand = Command(scenario, scenario.PreDispatch);
        Result<ReserveQuotaResult> first = await ledger
            .ReserveAsync(firstCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(first);
        M3E2EdgeFootprint before = await ReadM3E2EdgeFootprintAsync(
            scenario,
            firstCommand.RequestId.Value,
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);

        CrashAttempt gapAttempt = NewAttempt(60, 40, 20);
        ReserveQuotaCommand gap = Command(scenario, gapAttempt) with
        {
            RequestId = firstCommand.RequestId,
            AttemptIndex = 2,
        };
        Result<ReserveQuotaResult> rejected = await ledger
            .ReserveAsync(gap, cancellationToken)
            .ConfigureAwait(true);

        AssertFailure(rejected, "internal_error", retryAfterSeconds: null);
        Assert.Equal(
            before,
            await ReadM3E2EdgeFootprintAsync(
                scenario,
                firstCommand.RequestId.Value,
                scenario.PreDispatch.AttemptId,
                cancellationToken).ConfigureAwait(true));
        Assert.False(await ReservationExistsAsync(
            gapAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task MaximumNumeric78SettlementAndFactReplayWithoutTruncationOrDuplication()
    {
        // Governing contracts: AC-014/031 and database README sections 5-6.
        // The exact 78-digit upper bound must survive Npgsql, PostgreSQL counters,
        // the immutable fact reader, and an exact retry without duplicate outbox.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        DispatchedReservationHandle dispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            scenario,
            cancellationToken).ConfigureAwait(true);

        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        SettleReservationCommand command = SuccessfulSettlement(
            dispatch,
            new TokenUsage(
                maximumNumeric78,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            "upstream-request-max-numeric78");
        Result<QuotaTransitionResult> settlement = await ledger
            .SettleAsync(command, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(settlement);
        Assert.Equal(maximumNumeric78, settlement.Value.Quota.ConsumedTokens);
        Assert.Equal(BigInteger.Zero, settlement.Value.Quota.ReservedTokens);

        Result<QuotaTransitionResult> replay = await ledger
            .SettleAsync(command, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(replay);
        Assert.Equal(settlement.Value, replay.Value);

        AttemptSettlementFact fact = await ReadFactAsync(
            scenario.PreDispatch.AttemptId.AsEntityId(),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(maximumNumeric78, fact.Usage.Tokens.InputTokens);
        Assert.Equal(maximumNumeric78, fact.Usage.Tokens.TotalTokens);
        Assert.Equal(SettlementUsageSource.Upstream, fact.Usage.Source);
        Assert.False(fact.Usage.IsEstimated);
        Assert.Equal(Model, fact.RequestedModel);
        Assert.Equal(Model, fact.UpstreamModel);
        Assert.Equal(200, fact.UpstreamHttpStatus);
        Assert.False(fact.IsStreaming);

        M3E2ReplayEvidence evidence = await ReadM3E2ReplayEvidenceAsync(
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);
        string expected = maximumNumeric78.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(expected, evidence.ReservationActualTokens);
        Assert.Equal(expected, evidence.PeriodConsumedTokens);
        Assert.Equal(expected, evidence.AttemptInputTokens);
        Assert.Equal(expected, evidence.AttemptTotalTokens);
        Assert.Equal(expected, evidence.SettledDeltaTokens);
        Assert.Equal(1, evidence.AttemptCount);
        Assert.Equal(3, evidence.EventCount);
        Assert.Equal(3, evidence.EventOutboxCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Numeric78OverflowIsRejectedBeforeAnySettlementWrite()
    {
        // Governing contract: AC-031. An exact value above numeric(78,0) must be
        // rejected before opening the mutation UoW; the dispatch reservation stays intact.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        ReserveQuotaCommand reserveCommand = Command(scenario, scenario.PreDispatch);
        DispatchedReservationHandle dispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            scenario,
            cancellationToken).ConfigureAwait(true);
        M3E2EdgeFootprint before = await ReadM3E2EdgeFootprintAsync(
            scenario,
            reserveCommand.RequestId.Value,
            scenario.PreDispatch.AttemptId,
            cancellationToken).ConfigureAwait(true);

        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        SettleReservationCommand overflow = SuccessfulSettlement(
            dispatch,
            new TokenUsage(
                maximumNumeric78,
                BigInteger.One,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            "upstream-request-overflow");
        Result<QuotaTransitionResult> result = await ledger
            .SettleAsync(overflow, cancellationToken)
            .ConfigureAwait(true);

        AssertFailure(result, "token_numeric_overflow", retryAfterSeconds: null);
        Assert.Equal(
            before,
            await ReadM3E2EdgeFootprintAsync(
                scenario,
                reserveCommand.RequestId.Value,
                scenario.PreDispatch.AttemptId,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CumulativeNumeric78SettlementOverflowRollsBackEveryTerminalWrite()
    {
        // Governing contract: AC-031. Both upstream facts fit numeric(78,0), but
        // their cumulative period value does not. This reaches the PostgreSQL
        // counter-overflow branch rather than the application input preflight.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create() with { TotalTokens = 10 };
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        CrashAttempt firstAttempt = NewAttempt(1, 1, 0);
        CrashAttempt overflowingAttempt = NewAttempt(1, 1, 0);
        ReserveQuotaCommand firstCommand = Command(scenario, firstAttempt);
        ReserveQuotaCommand overflowingCommand = Command(scenario, overflowingAttempt);
        DispatchedReservationHandle firstDispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            firstCommand,
            cancellationToken).ConfigureAwait(true);
        DispatchedReservationHandle overflowingDispatch =
            await ReserveAndDispatchM3E2EdgeAsync(
                ledger,
                overflowingCommand,
                cancellationToken).ConfigureAwait(true);
        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        AssertSuccess(await ledger.SettleAsync(
            SuccessfulSettlement(
                firstDispatch,
                new TokenUsage(
                    maximumNumeric78,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero),
                "upstream-request-cumulative-maximum"),
            cancellationToken).ConfigureAwait(true));
        M3E2EdgeFootprint before = await ReadM3E2EdgeFootprintAsync(
            scenario,
            overflowingCommand.RequestId.Value,
            overflowingAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true);
        AttemptMutationCounts terminalBefore = await ReadAttemptMutationCountsAsync(
            overflowingAttempt.AttemptId,
            "settled",
            cancellationToken).ConfigureAwait(true);

        Result<QuotaTransitionResult> result = await ledger.SettleAsync(
            SuccessfulSettlement(
                overflowingDispatch,
                new TokenUsage(
                    BigInteger.One,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero),
                "upstream-request-cumulative-overflow"),
            cancellationToken).ConfigureAwait(true);

        AssertFailure(result, "token_numeric_overflow", retryAfterSeconds: null);
        M3E2EdgeFootprint after = await ReadM3E2EdgeFootprintAsync(
            scenario,
            overflowingCommand.RequestId.Value,
            overflowingAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(before, after);
        Assert.Equal("pending", after.ObservedReservationStatus);
        Assert.Null(after.ObservedActualTokens);
        Assert.Null(after.ObservedUsageSource);
        Assert.Equal(maximumNumeric78.ToString(CultureInfo.InvariantCulture), after.ConsumedTokens);
        Assert.Equal("1", after.ReservedTokens);
        Assert.Equal(
            terminalBefore,
            await ReadAttemptMutationCountsAsync(
                overflowingAttempt.AttemptId,
                "settled",
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(new AttemptMutationCounts(0, 0, 0, 0), terminalBefore);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CumulativeNumeric78AdjustmentOverflowRollsBackEveryCorrectionWrite()
    {
        // Governing contract: AC-031. The corrected fact is exactly the largest
        // legal numeric(78,0) value; another attempt already contributes one
        // Token, so applying the correction would overflow the period counter.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create() with { TotalTokens = 10 };
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        CrashAttempt firstAttempt = NewAttempt(1, 1, 0);
        CrashAttempt correctedAttempt = NewAttempt(1, 1, 0);
        DispatchedReservationHandle firstDispatch = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            Command(scenario, firstAttempt),
            cancellationToken).ConfigureAwait(true);
        ReserveQuotaCommand correctedCommand = Command(scenario, correctedAttempt);
        DispatchedReservationHandle correctedDispatch =
            await ReserveAndDispatchM3E2EdgeAsync(
                ledger,
                correctedCommand,
                cancellationToken).ConfigureAwait(true);
        AssertSuccess(await ledger.SettleAsync(
            SuccessfulSettlement(
                firstDispatch,
                new TokenUsage(1, 0, 0, 0, 0),
                "upstream-request-adjustment-peer"),
            cancellationToken).ConfigureAwait(true));
        SettleReservationCommand correctedBaseSettlement = SuccessfulSettlement(
            correctedDispatch,
            new TokenUsage(1, 0, 0, 0, 0),
            "upstream-request-adjustment-overflow");
        AssertSuccess(await ledger.SettleAsync(
            correctedBaseSettlement,
            cancellationToken).ConfigureAwait(true));
        M3E2EdgeFootprint before = await ReadM3E2EdgeFootprintAsync(
            scenario,
            correctedCommand.RequestId.Value,
            correctedAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true);
        AttemptMutationCounts correctionBefore = await ReadAttemptMutationCountsAsync(
            correctedAttempt.AttemptId,
            "usage_adjusted",
            cancellationToken).ConfigureAwait(true);
        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        IUsageAdjustmentWriter adjustmentWriter =
            _fixture.WorkerServices.GetRequiredService<IUsageAdjustmentWriter>();
        AdjustAttemptUsageCommand adjustment = new(
            scenario.GroupId.AsEntityId(),
            correctedAttempt.AttemptId.AsEntityId(),
            scenario.AccountId.AsEntityId(),
            scenario.ChannelId.AsEntityId(),
            SettlementProvider.OpenAi,
            Model,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            "upstream-request-adjustment-overflow",
            correctedDispatch.DispatchStartedAt,
            correctedBaseSettlement.FirstTokenAt,
            correctedBaseSettlement.CompletedAt,
            UsageRequestOutcome.Succeeded,
            new TokenUsage(
                maximumNumeric78,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.Upstream,
            null,
            "late usage would overflow cumulative period");

        Result<UsageAdjustmentResult> result = await adjustmentWriter
            .AdjustAsync(adjustment, cancellationToken)
            .ConfigureAwait(true);

        AssertFailure(result, "token_numeric_overflow", retryAfterSeconds: null);
        M3E2EdgeFootprint after = await ReadM3E2EdgeFootprintAsync(
            scenario,
            correctedCommand.RequestId.Value,
            correctedAttempt.AttemptId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(before, after);
        Assert.Equal("settled", after.ObservedReservationStatus);
        Assert.Equal("1", after.ObservedActualTokens);
        Assert.Equal("upstream", after.ObservedUsageSource);
        Assert.Equal("2", after.ConsumedTokens);
        Assert.Equal("0", after.ReservedTokens);
        Assert.Equal(
            correctionBefore,
            await ReadAttemptMutationCountsAsync(
                correctedAttempt.AttemptId,
                "usage_adjusted",
                cancellationToken).ConfigureAwait(true));
        Assert.Equal(new AttemptMutationCounts(1, 0, 0, 0), correctionBefore);
        AttemptSettlementFact fact = await ReadFactAsync(
            correctedAttempt.AttemptId.AsEntityId(),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(BigInteger.One, fact.Usage.Tokens.TotalTokens);
        Assert.Null(fact.Adjustment);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task NonUtcSettlementTimestampsAreNormalizedWithoutChangingTheInstant()
    {
        // Governing contract: timestamptz values represent instants. Npgsql requires
        // UTC DateTimeOffset values at the adapter boundary, while callers may hold
        // an equivalent offset representation.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        DispatchedReservationHandle dispatched = await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            scenario,
            cancellationToken).ConfigureAwait(true);
        TimeSpan offset = TimeSpan.FromHours(8);
        DispatchedReservationHandle offsetHandle = dispatched with
        {
            DispatchStartedAt = dispatched.DispatchStartedAt.ToOffset(offset),
        };
        DateTimeOffset firstTokenAt = offsetHandle.DispatchStartedAt.AddMilliseconds(1);
        SettleReservationCommand command = SuccessfulSettlement(
            offsetHandle,
            new TokenUsage(80, 50, 20, 10, 15),
            "upstream-request-offset") with
        {
            FirstTokenAt = firstTokenAt,
            CompletedAt = firstTokenAt.AddMilliseconds(1),
        };

        Result<QuotaTransitionResult> result = await ledger
            .SettleAsync(command, cancellationToken)
            .ConfigureAwait(true);

        AssertSuccess(result);
        AttemptSettlementFact fact = await ReadFactAsync(
            scenario.PreDispatch.AttemptId.AsEntityId(),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(offsetHandle.DispatchStartedAt, fact.DispatchStartedAt);
        Assert.Equal(command.FirstTokenAt, fact.FirstTokenAt);
        Assert.Equal(command.CompletedAt, fact.CompletedAt);
        Assert.Equal(TimeSpan.Zero, fact.DispatchStartedAt.Offset);
        Assert.Equal(TimeSpan.Zero, fact.FirstTokenAt?.Offset);
        Assert.Equal(TimeSpan.Zero, fact.CompletedAt.Offset);
    }

    private static SettleReservationCommand SuccessfulSettlement(
        DispatchedReservationHandle reservation,
        TokenUsage usage,
        string upstreamRequestId) => new(
            reservation,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            upstreamRequestId,
            null,
            reservation.DispatchStartedAt.AddMilliseconds(1),
            UsageRequestOutcome.Succeeded,
            usage,
            SettlementUsageSource.Upstream,
            null);

    private static async ValueTask<DispatchedReservationHandle>
        ReserveAndDispatchM3E2EdgeAsync(
            IGroupQuotaLedger ledger,
            CrashScenario scenario,
            CancellationToken cancellationToken) =>
        await ReserveAndDispatchM3E2EdgeAsync(
            ledger,
            Command(scenario, scenario.PreDispatch),
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<DispatchedReservationHandle>
        ReserveAndDispatchM3E2EdgeAsync(
            IGroupQuotaLedger ledger,
            ReserveQuotaCommand command,
            CancellationToken cancellationToken)
    {
        Result<ReserveQuotaResult> reserve = await ledger
            .ReserveAsync(command, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserve);
        Result<DispatchedReservationHandle> dispatch = await ledger
            .MarkDispatchedAsync(
                new MarkReservationDispatchedCommand(
                    reserve.Value.Reservation,
                    SettlementProvider.OpenAi,
                    Model,
                    new TokenEstimateSplit(
                        command.EstimatedTokens / 2,
                        command.EstimatedTokens - command.EstimatedTokens / 2)),
                cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(dispatch);
        return dispatch.Value;
    }

    private async ValueTask<M3E2EdgeFootprint> ReadM3E2EdgeFootprintAsync(
        CrashScenario scenario,
        Guid requestId,
        Guid observedAttemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                period.consumed_tokens::text,
                period.reserved_tokens::text,
                period.version,
                (SELECT count(*)::integer FROM public.usage_requests request
                 WHERE request.request_id = $3),
                (SELECT request.requested_model FROM public.usage_requests request
                 WHERE request.request_id = $3),
                (SELECT count(*)::integer FROM public.group_token_reservations reservation
                 WHERE reservation.request_id = $3),
                (SELECT count(*)::integer FROM public.usage_attempts attempt
                 WHERE attempt.request_id = $3),
                (SELECT count(*)::integer
                 FROM public.usage_attempt_adjustments adjustment
                 JOIN public.usage_attempts attempt
                   ON attempt.attempt_id = adjustment.attempt_id
                 WHERE attempt.request_id = $3),
                (SELECT count(*)::integer FROM public.group_quota_events event
                 WHERE event.group_id = $1),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 JOIN public.outbox_messages message
                   ON message.source_event_sequence = event.event_sequence
                  AND message.payload ->> 'event_id' = event.id::text
                 WHERE event.group_id = $1),
                (SELECT reservation.status FROM public.group_token_reservations reservation
                 WHERE reservation.attempt_id = $4),
                (SELECT reservation.actual_tokens::text
                 FROM public.group_token_reservations reservation
                 WHERE reservation.attempt_id = $4),
                (SELECT reservation.usage_source
                 FROM public.group_token_reservations reservation
                 WHERE reservation.attempt_id = $4)
            FROM public.group_token_quotas quota
            JOIN public.group_quota_periods period
              ON period.id = quota.current_period_id
             AND period.group_id = quota.group_id
            WHERE quota.group_id = $1 AND period.id = $2;
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(scenario.PeriodId);
        command.Parameters.AddWithValue(requestId);
        command.Parameters.AddWithValue(observedAttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E2EdgeFootprint result = ReadM3E2EdgeFootprint(reader);
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private static M3E2EdgeFootprint ReadM3E2EdgeFootprint(
        NpgsqlDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    private async ValueTask<M3E2ReplayEvidence> ReadM3E2ReplayEvidenceAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                reservation.actual_tokens::text,
                period.consumed_tokens::text,
                attempt.input_tokens::text,
                attempt.total_tokens::text,
                (SELECT event.delta_consumed_tokens::text
                 FROM public.group_quota_events event
                 WHERE event.attempt_id = reservation.attempt_id
                   AND event.event_type = 'settled'),
                (SELECT count(*)::integer FROM public.usage_attempts fact
                 WHERE fact.attempt_id = reservation.attempt_id),
                (SELECT count(*)::integer FROM public.group_quota_events event
                 WHERE event.attempt_id = reservation.attempt_id),
                (SELECT count(*)::integer
                 FROM public.group_quota_events event
                 JOIN public.outbox_messages message
                   ON message.source_event_sequence = event.event_sequence
                  AND message.payload ->> 'event_id' = event.id::text
                 WHERE event.attempt_id = reservation.attempt_id)
            FROM public.group_token_reservations reservation
            JOIN public.group_quota_periods period ON period.id = reservation.period_id
            JOIN public.usage_attempts attempt ON attempt.attempt_id = reservation.attempt_id
            WHERE reservation.attempt_id = $1;
            """);
        command.Parameters.AddWithValue(attemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        M3E2ReplayEvidence result = new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private async ValueTask<AttemptMutationCounts> ReadAttemptMutationCountsAsync(
        Guid attemptId,
        string eventType,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::integer FROM public.usage_attempts attempt
                 WHERE attempt.attempt_id = $1),
                (SELECT count(*)::integer FROM public.usage_attempt_adjustments adjustment
                 WHERE adjustment.attempt_id = $1),
                (SELECT count(*)::integer FROM public.group_quota_events event
                 WHERE event.attempt_id = $1 AND event.event_type = $2),
                (SELECT count(*)::integer FROM public.outbox_messages message
                 WHERE message.causation_id = $1 AND message.event_type = $2);
            """);
        command.Parameters.AddWithValue(attemptId);
        command.Parameters.AddWithValue(eventType);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        AttemptMutationCounts result = new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return result;
    }

    private async ValueTask<bool> ReservationExistsAsync(
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM public.group_token_reservations
                WHERE attempt_id = $1
            );
            """);
        command.Parameters.AddWithValue(attemptId);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private sealed record M3E2EdgeFootprint(
        string ConsumedTokens,
        string ReservedTokens,
        long PeriodVersion,
        int RequestCount,
        string? RequestedModel,
        int ReservationCount,
        int AttemptCount,
        int AdjustmentCount,
        int EventCount,
        int EventOutboxCount,
        string? ObservedReservationStatus,
        string? ObservedActualTokens,
        string? ObservedUsageSource);

    private sealed record M3E2ReplayEvidence(
        string ReservationActualTokens,
        string PeriodConsumedTokens,
        string AttemptInputTokens,
        string AttemptTotalTokens,
        string SettledDeltaTokens,
        int AttemptCount,
        int EventCount,
        int EventOutboxCount);

    private sealed record AttemptMutationCounts(
        int AttemptCount,
        int AdjustmentCount,
        int EventCount,
        int OutboxCount);
}
#pragma warning restore MA0051
