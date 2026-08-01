#pragma warning disable MA0051 // Each test keeps one complete quota transaction protocol visible.
using System.Numerics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/database/0002_quota_functions.sql, quota reservation and settlement ABI.
// - docs/开发执行规格-v1.0.md, M3-E2 reservation, dispatch, settlement, and release semantics.
public sealed class GroupQuotaLedgerServiceTests
{
    private const long MaximumSafeTokenCount = 9_007_199_254_740_991L;

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        1,
        4,
        0,
        0,
        TimeSpan.Zero);

    private static readonly EntityId RequestId = Id("018f3a4b-5c6d-7e8f-9123-000000000001");
    private static readonly EntityId AttemptId = Id("018f3a4b-5c6d-7e8f-9123-000000000002");
    private static readonly EntityId UserId = Id("018f3a4b-5c6d-7e8f-9123-000000000003");
    private static readonly EntityId ApiKeyId = Id("018f3a4b-5c6d-7e8f-9123-000000000004");
    private static readonly EntityId SubscriptionId = Id("018f3a4b-5c6d-7e8f-9123-000000000005");
    private static readonly EntityId GroupId = Id("018f3a4b-5c6d-7e8f-9123-000000000006");
    private static readonly EntityId AccountId = Id("018f3a4b-5c6d-7e8f-9123-000000000007");
    private static readonly EntityId ChannelId = Id("018f3a4b-5c6d-7e8f-9123-000000000008");
    private static readonly EntityId PeriodId = Id("018f3a4b-5c6d-7e8f-9123-000000000009");

    [Fact]
    public async Task ReserveUsesOneUnitOfWorkAndCommitsOnlyAfterRepositorySuccess()
    {
        ReserveQuotaCommand command = ReserveCommand(estimatedTokens: 100);
        EntityId reservationId = QuotaMutationIdentityFactory.ReservationId(command.AttemptId);
        QuotaReservationRow row = new(
            reservationId,
            PeriodId,
            ReservationStatus.Pending,
            TotalTokens: 1_000,
            ConsumedTokens: 100,
            ReservedTokens: 100,
            RemainingTokens: 800,
            LeaseExpiresAt: Now.AddMinutes(5),
            MaxExpiresAt: Now.AddMinutes(10));
        FakeQuotaLedgerRepository repository = new()
        {
            ReserveResult = QuotaRepositoryResult<QuotaReservationRow>.Success(row),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReserveQuotaResult> result = await service.ReserveAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Pending, result.Value.Status);
        Assert.Equal(reservationId, result.Value.Reservation.ReservationId);
        Assert.Equal(command.RequestId, result.Value.Reservation.RequestId);
        Assert.Equal(command.AttemptId, result.Value.Reservation.AttemptId);
        Assert.Equal(PeriodId, result.Value.Reservation.PeriodId);
        Assert.Equal((BigInteger)800, result.Value.Quota.RemainingTokens);
        Assert.Equal(1, repository.ReserveCalls);
        Assert.Equal(command, repository.LastReserve?.Command);
        Assert.Equal(reservationId, repository.LastReserve?.ReservationId);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastReserveContext);
    }

    [Fact]
    public async Task ReserveRepositoryFailureDoesNotCommit()
    {
        FakeQuotaLedgerRepository repository = new()
        {
            ReserveResult = QuotaRepositoryResult<QuotaReservationRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReserveQuotaResult> result = await service.ReserveAsync(
            ReserveCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(1, repository.ReserveCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastReserveContext);
    }

    [Theory]
    [InlineData(0L, false, false)]
    [InlineData(MaximumSafeTokenCount + 1, false, false)]
    [InlineData(1L, true, false)]
    [InlineData(1L, false, true)]
    public async Task InvalidReserveIsRejectedBeforeBeginningAUnitOfWork(
        long estimatedTokens,
        bool invalidRequestId,
        bool invalidAttemptId)
    {
        EntityId nonVersion7 = Id("018f3a4b-5c6d-4e8f-9123-000000000010");
        ReserveQuotaCommand command = ReserveCommand(estimatedTokens) with
        {
            RequestId = invalidRequestId ? nonVersion7 : RequestId,
            AttemptId = invalidAttemptId ? nonVersion7 : AttemptId,
        };
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter alerts = new();
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<ReserveQuotaResult> result = await service.ReserveAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.Null(result.Error.RetryAfterSeconds);
        Assert.Equal(0, repository.ReserveCalls);
        Assert.Equal(0, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
    }

    [Theory]
    [InlineData((int)QuotaLedgerFailure.QuotaExhausted, "group_quota_exhausted", null)]
    [InlineData((int)QuotaLedgerFailure.QuotaInsufficient, "group_quota_insufficient", null)]
    [InlineData((int)QuotaLedgerFailure.QuotaReserved, "group_quota_reserved", 1L)]
    public async Task ReserveMapsQuotaFailuresExactly(
        int failureValue,
        string expectedCode,
        long? expectedRetryAfterSeconds)
    {
        QuotaLedgerFailure failure = (QuotaLedgerFailure)failureValue;
        FakeQuotaLedgerRepository repository = new()
        {
            ReserveResult = QuotaRepositoryResult<QuotaReservationRow>.Failed(failure),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReserveQuotaResult> result = await service.ReserveAsync(
            ReserveCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedRetryAfterSeconds, result.Error.RetryAfterSeconds);
        Assert.Equal(1, repository.ReserveCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
    }

    [Fact]
    public async Task MarkDispatchedRejectsAnEstimateSplitThatDoesNotMatchReservation()
    {
        MarkReservationDispatchedCommand command = new(
            Reservation(estimatedTokens: 100),
            SettlementProvider.OpenAi,
            "gpt-5-mini",
            new TokenEstimateSplit(InputTokens: 40, OutputTokens: 40));
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<DispatchedReservationHandle> result = await service.MarkDispatchedAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("internal_error", result.Error.Code);
        Assert.Equal(0, repository.MarkDispatchedCalls);
        Assert.Equal(0, units.BeginCalls);
    }

    [Fact]
    public async Task MarkDispatchedMapsLostLeaseWithoutCommitting()
    {
        FakeQuotaLedgerRepository repository = new()
        {
            MarkDispatchedResult = QuotaRepositoryResult<QuotaDispatchRow>.Failed(
                QuotaLedgerFailure.ReservationLeaseLost),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);
        MarkReservationDispatchedCommand command = DispatchCommand();

        Result<DispatchedReservationHandle> result = await service.MarkDispatchedAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("reservation_lease_lost", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(1, repository.MarkDispatchedCalls);
        Assert.Equal(command, repository.LastMarkDispatched?.Command);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastMarkDispatchedContext);
    }

    [Fact]
    public async Task SuccessiveRenewalsUseDistinctDeterministicSequencesAndRetriesReuseOneMutation()
    {
        ReservationHandle reservation = Reservation();
        FakeQuotaLedgerRepository repository = new()
        {
            RenewResult = QuotaRepositoryResult<QuotaRenewalRow>.Success(new(
                reservation.ReservationId,
                reservation.PeriodId,
                ReservationStatus.Pending,
                Now.AddMinutes(7),
                reservation.MaxExpiresAt)),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReservationHandle> first = await service.RenewAsync(
            new RenewReservationCommand(reservation, RenewalSequence: 1),
            TestContext.Current.CancellationToken);
        Result<ReservationHandle> retry = await service.RenewAsync(
            new RenewReservationCommand(reservation, RenewalSequence: 1),
            TestContext.Current.CancellationToken);
        Result<ReservationHandle> second = await service.RenewAsync(
            new RenewReservationCommand(first.Value, RenewalSequence: 2),
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(Now.AddMinutes(7), second.Value.LeaseExpiresAt);
        Assert.Equal(3, repository.RenewCalls);
        Assert.Equal(3, units.BeginCalls);
        Assert.Equal(3, units.CommitCalls);
        Assert.Equal(3, units.DisposeCalls);
        Assert.Equal(repository.RenewWrites[0].Mutation, repository.RenewWrites[1].Mutation);
        Assert.NotEqual(repository.RenewWrites[0].Mutation, repository.RenewWrites[2].Mutation);
        Assert.Equal(
            $"quota:renew:v1:{AttemptId.Value:N}:1",
            repository.RenewWrites[0].Mutation.IdempotencyKey);
        Assert.Equal(
            $"quota:renew:v1:{AttemptId.Value:N}:2",
            repository.RenewWrites[2].Mutation.IdempotencyKey);
        Assert.All(
            repository.RenewWrites,
            static write => Assert.True(write.Command.RenewalSequence > 0));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task RenewRejectsNonPositiveSequenceBeforeBeginningAUnitOfWork(
        long renewalSequence)
    {
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReservationHandle> result = await service.RenewAsync(
            new RenewReservationCommand(Reservation(), renewalSequence),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("internal_error", result.Error.Code);
        Assert.Equal(0, repository.RenewCalls);
        Assert.Equal(0, units.BeginCalls);
    }

    [Fact]
    public async Task RenewRepositoryFailureRollsBackAndPreservesStableLeaseLostClassification()
    {
        FakeQuotaLedgerRepository repository = new()
        {
            RenewResult = QuotaRepositoryResult<QuotaRenewalRow>.Failed(
                QuotaLedgerFailure.ReservationLeaseLost),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        Result<ReservationHandle> result = await service.RenewAsync(
            new RenewReservationCommand(Reservation(), RenewalSequence: 1),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("reservation_lease_lost", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(1, repository.RenewCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastRenewContext);
    }

    [Fact]
    public async Task SettleAllowsActualUsageAboveTheReservationEstimate()
    {
        QuotaTransitionRow row = new(
            Reservation().ReservationId,
            PeriodId,
            ReservationStatus.Settled,
            TotalTokens: 1_000,
            ConsumedTokens: 130,
            ReservedTokens: 0,
            RemainingTokens: 870);
        FakeQuotaLedgerRepository repository = new()
        {
            SettleResult = QuotaRepositoryResult<QuotaTransitionRow>.Success(row),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);
        SettleReservationCommand command = SettlementCommand(
            new TokenUsage(
                InputTokens: 80,
                OutputTokens: 50,
                CacheReadTokens: 20,
                CacheCreationTokens: 10,
                ThinkingTokens: 15));

        Result<QuotaTransitionResult> result = await service.SettleAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Settled, result.Value.Status);
        Assert.Equal((BigInteger)130, repository.LastSettle?.Command.Usage.TotalTokens);
        Assert.Equal((BigInteger)130, result.Value.Quota.ConsumedTokens);
        Assert.Equal(1, repository.SettleCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastSettleContext);
    }

    [Fact]
    public async Task SettleRejectsNumeric78OverflowBeforeBeginningAUnitOfWork()
    {
        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        SettleReservationCommand command = SettlementCommand(
            new TokenUsage(
                maximumNumeric78,
                BigInteger.One,
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero));
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter alerts = new();
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<QuotaTransitionResult> result = await service.SettleAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("token_numeric_overflow", result.Error.Code);
        Assert.Equal(0, repository.SettleCalls);
        Assert.Equal(0, units.BeginCalls);
        Assert.Equal("group_quota.token_numeric_overflow", alerts.EventName);
        Assert.Equal("P0", alerts.Payload.GetProperty("severity").GetString());
        Assert.Equal("settle", alerts.Payload.GetProperty("operation").GetString());
        Assert.Equal(
            AttemptId.Value,
            alerts.Payload.GetProperty("attempt_id").GetGuid());
        Assert.Equal(CancellationToken.None, alerts.CancellationToken);
    }

    [Fact]
    public async Task SettleReportsDatabaseCounterOverflowAfterRollingBackTheUnitOfWork()
    {
        List<string> operationOrder = [];
        FakeQuotaLedgerRepository repository = new()
        {
            SettleResult = QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                QuotaLedgerFailure.TokenNumericOverflow),
        };
        RecordingUnitOfWorkFactory units = new(operationOrder);
        RecordingOperationalEventWriter alerts = new(operationOrder);
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<QuotaTransitionResult> result = await service.SettleAsync(
            SettlementCommand(new TokenUsage(80, 50, 20, 10, 15)),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("token_numeric_overflow", result.Error.Code);
        Assert.Equal(1, repository.SettleCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Equal("group_quota.token_numeric_overflow", alerts.EventName);
        Assert.Equal("P0", alerts.Payload.GetProperty("severity").GetString());
        // IUnitOfWork has no explicit rollback method: disposal without a commit is
        // the rollback boundary and must complete before the external P0 alert.
        Assert.Equal(
            ["unit-of-work.dispose", "operational-event.write"],
            operationOrder);
    }

    [Fact]
    public async Task StructurallyInvalidUsageDoesNotExposeOverflowClassification()
    {
        BigInteger maximumNumeric78 = BigInteger.Pow(10, 78) - BigInteger.One;
        SettleReservationCommand command = SettlementCommand(
            new TokenUsage(
                maximumNumeric78 + BigInteger.One,
                BigInteger.Zero,
                maximumNumeric78 + BigInteger.One,
                BigInteger.One,
                BigInteger.Zero));
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter alerts = new();
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<QuotaTransitionResult> result = await service.SettleAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("internal_error", result.Error.Code);
        Assert.Equal(0, repository.SettleCalls);
        Assert.Null(alerts.EventName);
    }

    [Fact]
    public async Task ConfirmedNoExecutionRequiresZeroUsageAndFailureEvidence()
    {
        SettleReservationCommand valid = ConfirmedNoExecutionCommand();
        SettleReservationCommand[] invalidCommands =
        [
            valid with
            {
                Usage = new TokenUsage(1, 0, 0, 0, 0),
            },
            valid with
            {
                AttemptOutcome = UsageAttemptOutcome.Succeeded,
            },
            valid with
            {
                ErrorCode = null,
            },
            valid with
            {
                FirstTokenAt = valid.Reservation.DispatchStartedAt.AddSeconds(1),
            },
            valid with
            {
                UpstreamHttpStatus = 500,
            },
        ];
        FakeQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);

        foreach (SettleReservationCommand command in invalidCommands)
        {
            Result<QuotaTransitionResult> rejected = await service.SettleAsync(
                command,
                TestContext.Current.CancellationToken);

            Assert.True(rejected.IsFailure);
            Assert.Equal("internal_error", rejected.Error.Code);
        }

        Assert.Equal(0, repository.SettleCalls);
        Assert.Equal(0, units.BeginCalls);

        QuotaTransitionRow row = new(
            valid.Reservation.Reservation.ReservationId,
            PeriodId,
            ReservationStatus.Settled,
            TotalTokens: 1_000,
            ConsumedTokens: 0,
            ReservedTokens: 0,
            RemainingTokens: 1_000);
        repository.SettleResult = QuotaRepositoryResult<QuotaTransitionRow>.Success(row);

        Result<QuotaTransitionResult> accepted = await service.SettleAsync(
            valid,
            TestContext.Current.CancellationToken);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(SettlementUsageSource.ConfirmedNoExecution,
            repository.LastSettle?.Command.UsageSource);
        Assert.Equal(BigInteger.Zero, repository.LastSettle?.Command.Usage.TotalTokens);
        Assert.Equal(1, repository.SettleCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
    }

    [Fact]
    public async Task ReleaseCommitsTheReleasedTransition()
    {
        ReservationHandle reservation = Reservation();
        QuotaTransitionRow row = new(
            reservation.ReservationId,
            PeriodId,
            ReservationStatus.Released,
            TotalTokens: 1_000,
            ConsumedTokens: 100,
            ReservedTokens: 0,
            RemainingTokens: 900);
        FakeQuotaLedgerRepository repository = new()
        {
            ReleaseResult = QuotaRepositoryResult<QuotaTransitionRow>.Success(row),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);
        ReleaseReservationCommand command = new(reservation, "upstream_not_started");

        Result<QuotaTransitionResult> result = await service.ReleaseAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationStatus.Released, result.Value.Status);
        Assert.Equal("upstream_not_started", repository.LastRelease?.Command.Reason);
        Assert.Equal(
            $"quota:release:v1:{AttemptId.Value:N}",
            repository.LastRelease?.Mutation.IdempotencyKey);
        Assert.Equal(1, repository.ReleaseCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastReleaseContext);
    }

    [Fact]
    public async Task LateUsageWithoutDispatchRaisesP0AndWritesNothing()
    {
        List<string> operationOrder = [];
        FakeQuotaLedgerRepository repository = new()
        {
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.UsageWithoutDispatch),
        };
        RecordingUnitOfWorkFactory units = new(operationOrder);
        RecordingOperationalEventWriter alerts = new(operationOrder);
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<UsageAdjustmentResult> result = await service.AdjustAsync(
            AdjustmentCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("internal_error", result.Error.Code);
        Assert.Equal(1, repository.AdjustmentCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Equal(
            ["unit-of-work.dispose", "operational-event.write"],
            operationOrder);
        Assert.Equal("group_quota.late_usage_invariant_violation", alerts.EventName);
        Assert.Equal("P0", alerts.Payload.GetProperty("severity").GetString());
        Assert.Equal(
            "pre_dispatch_usage",
            alerts.Payload.GetProperty("classification").GetString());
        Assert.Equal(
            AttemptId.Value,
            alerts.Payload.GetProperty("attempt_id").GetGuid());
    }

    [Fact]
    public async Task BrokenTerminalFactRaisesP0OnlyAfterTheUnitOfWorkRollsBack()
    {
        List<string> operationOrder = [];
        FakeQuotaLedgerRepository repository = new()
        {
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.TerminalFactInvariantBroken),
        };
        RecordingUnitOfWorkFactory units = new(operationOrder);
        RecordingOperationalEventWriter alerts = new(operationOrder);
        GroupQuotaLedgerService service = new(repository, units, alerts);

        Result<UsageAdjustmentResult> result = await service.AdjustAsync(
            AdjustmentCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(
            ["unit-of-work.dispose", "operational-event.write"],
            operationOrder);
        Assert.Equal(
            "terminal_fact_invariant",
            alerts.Payload.GetProperty("classification").GetString());
        Assert.Equal("group_quota.late_usage_invariant_violation", alerts.EventName);
        Assert.Equal("P0", alerts.Payload.GetProperty("severity").GetString());
        Assert.Equal(
            AttemptId.Value,
            alerts.Payload.GetProperty("attempt_id").GetGuid());
        Assert.Equal(CancellationToken.None, alerts.CancellationToken);
    }

    [Fact]
    public void MutationIdentitiesAreDeterministicVersion7AndOperationSeparated()
    {
        EntityId firstReservation = QuotaMutationIdentityFactory.ReservationId(AttemptId);
        EntityId secondReservation = QuotaMutationIdentityFactory.ReservationId(AttemptId);
        QuotaMutationIdentity firstReserve = QuotaMutationIdentityFactory.For(
            AttemptId,
            "reserve");
        QuotaMutationIdentity secondReserve = QuotaMutationIdentityFactory.For(
            AttemptId,
            "reserve");
        QuotaMutationIdentity settle = QuotaMutationIdentityFactory.For(
            AttemptId,
            "settle");

        Assert.Equal(firstReservation, secondReservation);
        Assert.Equal(firstReserve, secondReserve);
        Assert.All(
            new[]
            {
                firstReservation,
                firstReserve.EventId,
                firstReserve.OutboxId,
                settle.EventId,
                settle.OutboxId,
            },
            static identifier => Assert.Equal(7, identifier.Value.Version));
        Assert.Equal(5, new HashSet<EntityId>
        {
            firstReservation,
            firstReserve.EventId,
            firstReserve.OutboxId,
            settle.EventId,
            settle.OutboxId,
        }.Count);
        Assert.Equal($"quota:reserve:v1:{AttemptId.Value:N}", firstReserve.IdempotencyKey);
        Assert.Equal($"quota:settle:v1:{AttemptId.Value:N}", settle.IdempotencyKey);
        Assert.NotEqual(firstReserve.IdempotencyKey, settle.IdempotencyKey);
    }

    [Fact]
    public async Task SettlementFactReaderReusesTheCallersUnitOfWorkContext()
    {
        AttemptSettlementFact fact = SettlementFact();
        FakeQuotaLedgerRepository repository = new()
        {
            SettlementFact = fact,
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units);
        CallerUnitOfWorkContext callerContext = new();

        Result<AttemptSettlementFact> result = await service.GetByAttemptIdAsync(
            AttemptId,
            callerContext,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(fact, result.Value);
        Assert.Equal(AttemptId, repository.LastSettlementFactAttemptId);
        Assert.Same(callerContext, repository.LastSettlementFactContext);
        Assert.Equal(1, repository.SettlementFactCalls);
        Assert.Equal(0, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
    }

    private static ReserveQuotaCommand ReserveCommand(long estimatedTokens = 100) => new(
        RequestId,
        AttemptId,
        AttemptIndex: 1,
        UserId,
        ApiKeyId,
        SubscriptionId,
        GroupId,
        AccountId,
        ChannelId,
        UsageRequestEndpoint.Responses,
        RequestedModel: "gpt-5-mini",
        ClientRequestId: "client-request-1",
        EstimatedTokens: estimatedTokens,
        IsStreaming: true,
        LeaseOwner: "gateway-1");

    private static ReservationHandle Reservation(long estimatedTokens = 100) => new(
        QuotaMutationIdentityFactory.ReservationId(AttemptId),
        RequestId,
        AttemptId,
        AttemptIndex: 1,
        GroupId,
        PeriodId,
        AccountId,
        ChannelId,
        EstimatedTokens: estimatedTokens,
        IsStreaming: true,
        LeaseOwner: "gateway-1",
        LeaseExpiresAt: Now.AddMinutes(5),
        MaxExpiresAt: Now.AddMinutes(10));

    private static MarkReservationDispatchedCommand DispatchCommand() => new(
        Reservation(),
        SettlementProvider.OpenAi,
        "gpt-5-mini",
        new TokenEstimateSplit(InputTokens: 60, OutputTokens: 40));

    private static DispatchedReservationHandle DispatchedReservation() => new(
        ReservationStatus.Pending,
        Reservation(),
        SettlementProvider.OpenAi,
        "gpt-5-mini",
        new TokenEstimateSplit(InputTokens: 60, OutputTokens: 40),
        DispatchStartedAt: Now.AddMinutes(1));

    private static SettleReservationCommand SettlementCommand(TokenUsage usage) => new(
        DispatchedReservation(),
        UsageAttemptOutcome.Succeeded,
        UpstreamHttpStatus: 200,
        ErrorCode: null,
        UpstreamRequestId: "upstream-request-1",
        FirstTokenAt: Now.AddMinutes(1).AddSeconds(1),
        CompletedAt: Now.AddMinutes(2),
        RequestOutcome: UsageRequestOutcome.Succeeded,
        Usage: usage,
        UsageSource: SettlementUsageSource.Upstream,
        RawUpstreamUsage: null);

    private static SettleReservationCommand ConfirmedNoExecutionCommand() => new(
        DispatchedReservation(),
        UsageAttemptOutcome.Failed,
        UpstreamHttpStatus: 401,
        ErrorCode: "invalid_api_key",
        UpstreamRequestId: null,
        FirstTokenAt: null,
        CompletedAt: Now.AddMinutes(2),
        RequestOutcome: UsageRequestOutcome.Failed,
        Usage: new TokenUsage(0, 0, 0, 0, 0),
        UsageSource: SettlementUsageSource.ConfirmedNoExecution,
        RawUpstreamUsage: null);

    private static AdjustAttemptUsageCommand AdjustmentCommand() => new(
        GroupId,
        AttemptId,
        AccountId,
        ChannelId,
        SettlementProvider.OpenAi,
        "gpt-5-mini",
        UsageAttemptOutcome.Succeeded,
        UpstreamHttpStatus: 200,
        ErrorCode: null,
        UpstreamRequestId: "upstream-request-1",
        DispatchStartedAt: Now.AddMinutes(1),
        FirstTokenAt: Now.AddMinutes(1).AddSeconds(1),
        CompletedAt: Now.AddMinutes(2),
        RequestOutcome: UsageRequestOutcome.Succeeded,
        CorrectedUsage: new TokenUsage(80, 50, 20, 10, 15),
        UsageSource: SettlementUsageSource.Upstream,
        RawUpstreamUsage: null,
        Reason: "late authoritative usage");

    private static AttemptSettlementFact SettlementFact() => new(
        AttemptId,
        RequestId,
        AttemptIndex: 1,
        Reservation().ReservationId,
        GroupId,
        PeriodId,
        AccountId,
        ChannelId,
        SettlementProvider.OpenAi,
        RequestedModel: "gpt-5-mini",
        UpstreamModel: "gpt-5-mini-2026-07-01",
        UsageAttemptOutcome.Succeeded,
        UpstreamHttpStatus: 200,
        ErrorCode: null,
        IsStreaming: false,
        Usage: new AttemptUsage(
            new TokenUsage(80, 50, 20, 10, 15),
            SettlementUsageSource.Upstream,
            IsEstimated: false),
        Adjustment: null,
        DispatchStartedAt: Now.AddMinutes(1),
        FirstTokenAt: Now.AddMinutes(1).AddSeconds(1),
        CompletedAt: Now.AddMinutes(2));

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed class FakeQuotaLedgerRepository : IQuotaLedgerRepository
    {
        internal QuotaRepositoryResult<QuotaReservationRow>? ReserveResult { get; set; }

        internal QuotaRepositoryResult<QuotaDispatchRow>? MarkDispatchedResult { get; set; }

        internal QuotaRepositoryResult<QuotaRenewalRow>? RenewResult { get; set; }
        internal QuotaRepositoryResult<QuotaTransitionRow>? SettleResult { get; set; }

        internal QuotaRepositoryResult<QuotaTransitionRow>? ReleaseResult { get; set; }

        internal QuotaRepositoryResult<UsageAdjustmentRow>? AdjustmentResult { get; set; }

        internal AttemptSettlementFact? SettlementFact { get; set; }

        internal int ReserveCalls { get; private set; }

        internal int MarkDispatchedCalls { get; private set; }

        internal int RenewCalls { get; private set; }

        internal int SettleCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal int AdjustmentCalls { get; private set; }

        internal int SettlementFactCalls { get; private set; }

        internal ReserveQuotaWrite? LastReserve { get; private set; }

        internal MarkReservationDispatchedWrite? LastMarkDispatched { get; private set; }

        internal List<RenewReservationWrite> RenewWrites { get; } = [];

        internal SettleReservationWrite? LastSettle { get; private set; }

        internal ReleaseReservationWrite? LastRelease { get; private set; }

        internal AdjustAttemptUsageWrite? LastAdjustment { get; private set; }

        internal IUnitOfWorkContext? LastReserveContext { get; private set; }

        internal IUnitOfWorkContext? LastMarkDispatchedContext { get; private set; }

        internal IUnitOfWorkContext? LastRenewContext { get; private set; }

        internal IUnitOfWorkContext? LastSettleContext { get; private set; }

        internal IUnitOfWorkContext? LastReleaseContext { get; private set; }

        internal EntityId? LastSettlementFactAttemptId { get; private set; }

        internal IUnitOfWorkContext? LastSettlementFactContext { get; private set; }

        public ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
            ReserveQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReserveCalls++;
            LastReserve = write;
            LastReserveContext = unitOfWorkContext;
            return ValueTask.FromResult(ReserveResult ?? throw Unexpected(nameof(ReserveAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaDispatchRow>> MarkDispatchedAsync(
            MarkReservationDispatchedWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkDispatchedCalls++;
            LastMarkDispatched = write;
            LastMarkDispatchedContext = unitOfWorkContext;
            return ValueTask.FromResult(
                MarkDispatchedResult ?? throw Unexpected(nameof(MarkDispatchedAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaRenewalRow>> RenewAsync(
            RenewReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenewCalls++;
            RenewWrites.Add(write);
            LastRenewContext = unitOfWorkContext;
            return ValueTask.FromResult(
                RenewResult ?? throw Unexpected(nameof(RenewAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
            SettleReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SettleCalls++;
            LastSettle = write;
            LastSettleContext = unitOfWorkContext;
            return ValueTask.FromResult(SettleResult ?? throw Unexpected(nameof(SettleAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
            ReleaseReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCalls++;
            LastRelease = write;
            LastReleaseContext = unitOfWorkContext;
            return ValueTask.FromResult(ReleaseResult ?? throw Unexpected(nameof(ReleaseAsync)));
        }

        public ValueTask<IReadOnlyList<QuotaExpiryCandidate>> ListDueExpiryCandidatesAsync(
            QuotaExpiryCandidateKey? after,
            int pageSize,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(
                nameof(ListDueExpiryCandidatesAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ExpireAsync(
            ExpireReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(ExpireAsync));

        public ValueTask<QuotaRepositoryResult<UsageAdjustmentRow>> AdjustUsageAsync(
            AdjustAttemptUsageWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdjustmentCalls++;
            LastAdjustment = write;
            return ValueTask.FromResult(
                AdjustmentResult ?? throw Unexpected(nameof(AdjustUsageAsync)));
        }

        public ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
            EntityId attemptId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SettlementFactCalls++;
            LastSettlementFactAttemptId = attemptId;
            LastSettlementFactContext = unitOfWorkContext;
            return ValueTask.FromResult(SettlementFact);
        }

        private static InvalidOperationException Unexpected(string operation) => new(
            $"The {operation} repository result was not configured.");
    }

    private sealed class RecordingUnitOfWorkFactory(
        ICollection<string>? operationOrder = null) : IUnitOfWorkFactory
    {
        private readonly ICollection<string>? _operationOrder = operationOrder;

        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal IUnitOfWorkContext? LastContext { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            UnitOfWorkContext context = new();
            LastContext = context;
            return ValueTask.FromResult<IUnitOfWork>(
                new UnitOfWork(this, context, _operationOrder));
        }

        private sealed class UnitOfWork(
            RecordingUnitOfWorkFactory owner,
            IUnitOfWorkContext context,
            ICollection<string>? operationOrder) : IUnitOfWork
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
                operationOrder?.Add("unit-of-work.dispose");
                return ValueTask.CompletedTask;
            }
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class CallerUnitOfWorkContext : IUnitOfWorkContext;

    private sealed class RecordingOperationalEventWriter(
        ICollection<string>? operationOrder = null) : IOperationalEventWriter
    {
        internal string? EventName { get; private set; }

        internal JsonElement Payload { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationOrder?.Add("operational-event.write");
            EventName = eventName;
            Payload = payload;
            CancellationToken = cancellationToken;
            return ValueTask.CompletedTask;
        }
    }
}
#pragma warning restore MA0051
