#pragma warning disable MA0051 // The contract matrices intentionally keep every short-circuit case visible.
using System.Numerics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/database/README.md, reservation/settlement identity and numeric ABI.
// - docs/开发执行规格-v1.0.md, DEC-011/013/017/038 and M3-E2.
public sealed class GroupQuotaLedgerValidationCoverageTests
{
    private const long MaximumSafeTokenCount = 9_007_199_254_740_991L;

    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        1,
        6,
        0,
        0,
        TimeSpan.Zero);

    private static readonly EntityId RequestId = Id(1);
    private static readonly EntityId AttemptId = Id(2);
    private static readonly EntityId ReservationId = Id(3);
    private static readonly EntityId UserId = Id(4);
    private static readonly EntityId ApiKeyId = Id(5);
    private static readonly EntityId SubscriptionId = Id(6);
    private static readonly EntityId GroupId = Id(7);
    private static readonly EntityId PeriodId = Id(8);
    private static readonly EntityId AccountId = Id(9);
    private static readonly EntityId ChannelId = Id(10);
    private static readonly EntityId NonVersion7 = new(
        Guid.Parse("018f3a4b-5c6d-4e8f-9123-000000000011"));
    private static readonly JsonElement NonObjectRawUsage =
        JsonSerializer.SerializeToElement(new[] { 1 });

    [Fact]
    public void ReserveValidationCoversEveryIdentityRoutingAndEstimateBoundary()
    {
        ReserveQuotaCommand valid = ReserveCommand();

        Assert.True(QuotaLedgerValidation.IsValid(valid));
        Assert.True(QuotaLedgerValidation.IsValid(valid with
        {
            ClientRequestId = null,
            EstimatedTokens = 1,
        }));
        Assert.True(QuotaLedgerValidation.IsValid(valid with
        {
            EstimatedTokens = MaximumSafeTokenCount,
        }));

        (string Label, ReserveQuotaCommand Command)[] invalid =
        [
            ("request UUID version", valid with { RequestId = NonVersion7 }),
            ("attempt UUID version", valid with { AttemptId = NonVersion7 }),
            ("attempt index", valid with { AttemptIndex = -1 }),
            ("endpoint", valid with { Endpoint = (UsageRequestEndpoint)int.MaxValue }),
            ("requested model", valid with { RequestedModel = "   " }),
            ("client request id", valid with { ClientRequestId = "" }),
            ("zero estimate", valid with { EstimatedTokens = 0 }),
            ("unsafe estimate", valid with
            {
                EstimatedTokens = MaximumSafeTokenCount + 1,
            }),
            ("lease owner", valid with { LeaseOwner = "\t" }),
        ];

        foreach ((string label, ReserveQuotaCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.IsValid(command), label);
        }
    }

    [Fact]
    public void DispatchValidationCoversEveryReservationAndEstimateFence()
    {
        MarkReservationDispatchedCommand valid = DispatchCommand();
        ReservationHandle reservation = valid.Reservation;

        Assert.True(QuotaLedgerValidation.IsValid(valid));
        Assert.True(QuotaLedgerValidation.IsValid(valid with
        {
            Reservation = reservation with
            {
                EstimatedTokens = MaximumSafeTokenCount,
                LeaseExpiresAt = Now.AddMinutes(10),
                MaxExpiresAt = Now.AddMinutes(10),
            },
            Estimate = new TokenEstimateSplit(MaximumSafeTokenCount, 0),
        }));

        (string Label, MarkReservationDispatchedCommand Command)[] invalid =
        [
            ("missing reservation", valid with { Reservation = null! }),
            ("missing estimate", valid with { Estimate = null! }),
            ("request UUID version", valid with
            {
                Reservation = reservation with { RequestId = NonVersion7 },
            }),
            ("attempt UUID version", valid with
            {
                Reservation = reservation with { AttemptId = NonVersion7 },
            }),
            ("reservation UUID version", valid with
            {
                Reservation = reservation with { ReservationId = NonVersion7 },
            }),
            ("attempt index", valid with
            {
                Reservation = reservation with { AttemptIndex = -1 },
            }),
            ("zero reservation estimate", valid with
            {
                Reservation = reservation with { EstimatedTokens = 0 },
            }),
            ("unsafe reservation estimate", valid with
            {
                Reservation = reservation with
                {
                    EstimatedTokens = MaximumSafeTokenCount + 1,
                },
            }),
            ("lease owner", valid with
            {
                Reservation = reservation with { LeaseOwner = "" },
            }),
            ("lease deadline order", valid with
            {
                Reservation = reservation with
                {
                    LeaseExpiresAt = Now.AddMinutes(11),
                    MaxExpiresAt = Now.AddMinutes(10),
                },
            }),
            ("provider", valid with { Provider = (SettlementProvider)int.MaxValue }),
            ("model", valid with { Model = " " }),
            ("negative input estimate", valid with
            {
                Estimate = new TokenEstimateSplit(-1, 101),
            }),
            ("negative output estimate", valid with
            {
                Estimate = new TokenEstimateSplit(101, -1),
            }),
            ("unsafe input estimate", valid with
            {
                Estimate = new TokenEstimateSplit(MaximumSafeTokenCount + 1, 0),
            }),
            ("unsafe output estimate", valid with
            {
                Estimate = new TokenEstimateSplit(0, MaximumSafeTokenCount + 1),
            }),
            ("estimate split", valid with
            {
                Estimate = new TokenEstimateSplit(60, 39),
            }),
        ];

        foreach ((string label, MarkReservationDispatchedCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.IsValid(command), label);
        }
    }

    [Fact]
    public void RenewValidationCoversReservationShapeAndPositiveSequence()
    {
        ReservationHandle reservation = Reservation();
        RenewReservationCommand valid = new(reservation, RenewalSequence: 1);

        Assert.True(QuotaLedgerValidation.IsValid(valid));
        (string Label, RenewReservationCommand Command)[] invalid =
        [
            ("missing reservation", valid with { Reservation = null! }),
            ("request UUID version", valid with
            {
                Reservation = reservation with { RequestId = NonVersion7 },
            }),
            ("attempt UUID version", valid with
            {
                Reservation = reservation with { AttemptId = NonVersion7 },
            }),
            ("reservation UUID version", valid with
            {
                Reservation = reservation with { ReservationId = NonVersion7 },
            }),
            ("attempt index", valid with
            {
                Reservation = reservation with { AttemptIndex = -1 },
            }),
            ("zero estimate", valid with
            {
                Reservation = reservation with { EstimatedTokens = 0 },
            }),
            ("unsafe estimate", valid with
            {
                Reservation = reservation with
                {
                    EstimatedTokens = MaximumSafeTokenCount + 1,
                },
            }),
            ("lease owner", valid with
            {
                Reservation = reservation with { LeaseOwner = " " },
            }),
            ("lease deadline order", valid with
            {
                Reservation = reservation with
                {
                    LeaseExpiresAt = Now.AddMinutes(11),
                    MaxExpiresAt = Now.AddMinutes(10),
                },
            }),
            ("zero renewal sequence", valid with { RenewalSequence = 0 }),
            ("negative renewal sequence", valid with { RenewalSequence = -1 }),
        ];

        foreach ((string label, RenewReservationCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.IsValid(command), label);
        }
    }

    [Fact]
    public void SettlementStructureCoversEveryOptionalFieldTokenAndClockFence()
    {
        SettleReservationCommand valid = SettlementCommand();
        DispatchedReservationHandle dispatched = valid.Reservation;
        ReservationHandle reservation = dispatched.Reservation;

        Assert.True(QuotaLedgerValidation.HasValidStructure(valid));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = null,
            ErrorCode = null,
            UpstreamRequestId = null,
            FirstTokenAt = null,
            RequestOutcome = null,
            RawUpstreamUsage = null,
        }));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = 100,
        }));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = 599,
        }));

        List<(string Label, SettleReservationCommand Command)> invalid =
        [
            ("missing reservation", valid with { Reservation = null! }),
            ("missing usage", valid with { Usage = null! }),
            ("reservation", valid with
            {
                Reservation = dispatched with
                {
                    Reservation = reservation with { AttemptIndex = -1 },
                },
            }),
            ("provider", valid with
            {
                Reservation = dispatched with
                {
                    Provider = (SettlementProvider)int.MaxValue,
                },
            }),
            ("attempt outcome", valid with
            {
                AttemptOutcome = (UsageAttemptOutcome)int.MaxValue,
            }),
            ("usage source", valid with
            {
                UsageSource = (SettlementUsageSource)int.MaxValue,
            }),
            ("request outcome", valid with
            {
                RequestOutcome = (UsageRequestOutcome)int.MaxValue,
            }),
            ("low HTTP status", valid with { UpstreamHttpStatus = 99 }),
            ("high HTTP status", valid with { UpstreamHttpStatus = 600 }),
            ("blank error code", valid with { ErrorCode = " " }),
            ("blank upstream request id", valid with { UpstreamRequestId = "" }),
            ("raw usage shape", valid with
            {
                RawUpstreamUsage = NonObjectRawUsage,
            }),
            ("completion before dispatch", valid with
            {
                CompletedAt = dispatched.DispatchStartedAt.AddTicks(-1),
                FirstTokenAt = null,
            }),
            ("first token before dispatch", valid with
            {
                FirstTokenAt = dispatched.DispatchStartedAt.AddTicks(-1),
            }),
            ("first token after completion", valid with
            {
                FirstTokenAt = valid.CompletedAt.AddTicks(1),
            }),
        ];
        invalid.AddRange(InvalidTokenUsages().Select(item =>
            ($"usage {item.Label}", valid with { Usage = item.Usage })));

        foreach ((string label, SettleReservationCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.HasValidStructure(command), label);
        }
    }

    [Fact]
    public void ConfirmedNoExecutionSettlementCoversEveryConservativeGuard()
    {
        SettleReservationCommand valid = ConfirmedNoExecutionSettlement();

        Assert.True(QuotaLedgerValidation.HasValidStructure(valid));
        foreach (int? status in new int?[] { null, 401, 403, 429 })
        {
            Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
            {
                UpstreamHttpStatus = status,
            }));
        }

        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            AttemptOutcome = UsageAttemptOutcome.Cancelled,
        }));

        (string Label, SettleReservationCommand Command)[] invalid =
        [
            ("nonzero usage", valid with { Usage = Usage(1, 0) }),
            ("successful outcome", valid with
            {
                AttemptOutcome = UsageAttemptOutcome.Succeeded,
            }),
            ("missing error", valid with { ErrorCode = null }),
            ("first token", valid with
            {
                FirstTokenAt = valid.Reservation.DispatchStartedAt,
            }),
            ("disallowed HTTP status", valid with { UpstreamHttpStatus = 200 }),
        ];

        foreach ((string label, SettleReservationCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.HasValidStructure(command), label);
        }
    }

    [Fact]
    public void AdjustmentStructureCoversEveryIdentityFieldTokenAndClockFence()
    {
        AdjustAttemptUsageCommand valid = AdjustmentCommand();

        Assert.True(QuotaLedgerValidation.HasValidStructure(valid));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = null,
            ErrorCode = null,
            UpstreamRequestId = null,
            FirstTokenAt = null,
            RequestOutcome = null,
            RawUpstreamUsage = null,
        }));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = 100,
        }));
        Assert.True(QuotaLedgerValidation.HasValidStructure(valid with
        {
            UpstreamHttpStatus = 599,
        }));

        List<(string Label, AdjustAttemptUsageCommand Command)> invalid =
        [
            ("missing usage", valid with { CorrectedUsage = null! }),
            ("attempt UUID version", valid with { AttemptId = NonVersion7 }),
            ("provider", valid with { Provider = (SettlementProvider)int.MaxValue }),
            ("model", valid with { Model = "" }),
            ("attempt outcome", valid with
            {
                AttemptOutcome = (UsageAttemptOutcome)int.MaxValue,
            }),
            ("usage source", valid with
            {
                UsageSource = (SettlementUsageSource)int.MaxValue,
            }),
            ("request outcome", valid with
            {
                RequestOutcome = (UsageRequestOutcome)int.MaxValue,
            }),
            ("low HTTP status", valid with { UpstreamHttpStatus = 99 }),
            ("high HTTP status", valid with { UpstreamHttpStatus = 600 }),
            ("blank error code", valid with { ErrorCode = " " }),
            ("blank upstream request id", valid with { UpstreamRequestId = "" }),
            ("raw usage shape", valid with
            {
                RawUpstreamUsage = JsonSerializer.SerializeToElement("invalid"),
            }),
            ("completion before dispatch", valid with
            {
                CompletedAt = valid.DispatchStartedAt.AddTicks(-1),
                FirstTokenAt = null,
            }),
            ("first token before dispatch", valid with
            {
                FirstTokenAt = valid.DispatchStartedAt.AddTicks(-1),
            }),
            ("first token after completion", valid with
            {
                FirstTokenAt = valid.CompletedAt.AddTicks(1),
            }),
            ("reason", valid with { Reason = "\t" }),
        ];
        invalid.AddRange(InvalidTokenUsages().Select(item =>
            ($"usage {item.Label}", valid with { CorrectedUsage = item.Usage })));

        foreach ((string label, AdjustAttemptUsageCommand command) in invalid)
        {
            Assert.False(QuotaLedgerValidation.HasValidStructure(command), label);
        }
    }

    [Fact]
    public void ConfirmedNoExecutionAdjustmentRequiresAnExactZeroCorrection()
    {
        AdjustAttemptUsageCommand valid = AdjustmentCommand() with
        {
            AttemptOutcome = UsageAttemptOutcome.Cancelled,
            UpstreamHttpStatus = null,
            ErrorCode = "cancelled_before_execution",
            FirstTokenAt = null,
            RequestOutcome = UsageRequestOutcome.Cancelled,
            CorrectedUsage = Usage(0, 0),
            UsageSource = SettlementUsageSource.ConfirmedNoExecution,
            RawUpstreamUsage = null,
        };

        Assert.True(QuotaLedgerValidation.HasValidStructure(valid));
        Assert.False(QuotaLedgerValidation.HasValidStructure(valid with
        {
            CorrectedUsage = Usage(1, 0),
        }));
    }

    [Fact]
    public void TokenValidationAndNumeric78DetectionCoverEveryComponent()
    {
        BigInteger maximum = QuotaLedgerValidation.MaximumNumeric78;

        Assert.True(QuotaLedgerValidation.IsValid(Usage(80, 50)));
        Assert.True(QuotaLedgerValidation.IsValid(new TokenUsage(
            maximum,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero)));
        foreach ((string label, TokenUsage usage) in InvalidTokenUsages())
        {
            Assert.False(QuotaLedgerValidation.IsValid(usage), label);
        }

        Assert.False(QuotaLedgerValidation.ExceedsNumeric78(Usage(80, 50)));
        (string Label, TokenUsage Usage)[] overflow =
        [
            ("input", new TokenUsage(maximum + 1, 0, 0, 0, 0)),
            ("output", new TokenUsage(0, maximum + 1, 0, 0, 0)),
            ("cache read", new TokenUsage(0, 0, maximum + 1, 0, 0)),
            ("cache creation", new TokenUsage(0, 0, 0, maximum + 1, 0)),
            ("thinking", new TokenUsage(0, 0, 0, 0, maximum + 1)),
            ("total", new TokenUsage(maximum, 1, 0, 0, 0)),
        ];
        foreach ((string label, TokenUsage usage) in overflow)
        {
            Assert.True(QuotaLedgerValidation.ExceedsNumeric78(usage), label);
        }

        Assert.True(QuotaLedgerValidation.IsValid(SettlementCommand()));
        Assert.False(QuotaLedgerValidation.IsValid(SettlementCommand() with
        {
            Usage = new TokenUsage(maximum, 1, 0, 0, 0),
        }));
        Assert.True(QuotaLedgerValidation.IsValid(AdjustmentCommand()));
        Assert.False(QuotaLedgerValidation.IsValid(AdjustmentCommand() with
        {
            CorrectedUsage = new TokenUsage(maximum, 1, 0, 0, 0),
        }));
    }

    [Fact]
    public void ReleaseValidationCoversNullReservationStateAndReason()
    {
        ReleaseReservationCommand valid = ReleaseCommand();

        Assert.True(QuotaLedgerValidation.IsValid(valid));
        Assert.False(QuotaLedgerValidation.IsValid(valid with { Reservation = null! }));
        Assert.False(QuotaLedgerValidation.IsValid(valid with
        {
            Reservation = valid.Reservation with { AttemptId = NonVersion7 },
        }));
        Assert.False(QuotaLedgerValidation.IsValid(valid with { Reason = " " }));
    }

    [Fact]
    public void MutationIdentityGuardsOperationsAndAlwaysDerivesVersion7Ids()
    {
        Assert.Throws<ArgumentNullException>(
            () => QuotaMutationIdentityFactory.For(AttemptId, null!));
        Assert.Throws<ArgumentException>(
            () => QuotaMutationIdentityFactory.For(AttemptId, string.Empty));
        Assert.Throws<ArgumentException>(
            () => QuotaMutationIdentityFactory.For(AttemptId, "   "));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QuotaMutationIdentityFactory.ForRenewal(AttemptId, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => QuotaMutationIdentityFactory.ForRenewal(AttemptId, -1));

        EntityId derivedFromNonVersion7 =
            QuotaMutationIdentityFactory.ReservationId(NonVersion7);
        QuotaMutationIdentity mutation =
            QuotaMutationIdentityFactory.For(NonVersion7, "reserve");
        QuotaMutationIdentity renewal =
            QuotaMutationIdentityFactory.ForRenewal(NonVersion7, 17);
        QuotaMutationIdentity renewalReplay =
            QuotaMutationIdentityFactory.ForRenewal(NonVersion7, 17);
        QuotaMutationIdentity nextRenewal =
            QuotaMutationIdentityFactory.ForRenewal(NonVersion7, 18);

        Assert.Equal(7, derivedFromNonVersion7.Value.Version);
        Assert.Equal(7, mutation.EventId.Value.Version);
        Assert.Equal(7, mutation.OutboxId.Value.Version);
        Assert.Equal(renewal, renewalReplay);
        Assert.NotEqual(renewal, nextRenewal);
        Assert.Equal(7, renewal.EventId.Value.Version);
        Assert.Equal(7, renewal.OutboxId.Value.Version);
        Assert.Equal(
            $"quota:reserve:v1:{NonVersion7.Value:N}",
            mutation.IdempotencyKey);
        Assert.Equal(
            $"quota:renew:v1:{NonVersion7.Value:N}:17",
            renewal.IdempotencyKey);
    }

    [Fact]
    public void RepositoryResultFactoriesRejectImpossibleSuccessAndFailureShapes()
    {
        QuotaReservationRow row = ReservationRow();
        QuotaRepositoryResult<QuotaReservationRow> success =
            QuotaRepositoryResult<QuotaReservationRow>.Success(row);
        QuotaRepositoryResult<QuotaReservationRow> failure =
            QuotaRepositoryResult<QuotaReservationRow>.Failed(
                QuotaLedgerFailure.Internal);

        Assert.True(success.IsSuccess);
        Assert.Same(row, success.Value);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Throws<ArgumentNullException>(
            static () => QuotaRepositoryResult<QuotaReservationRow>.Success(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            static () => QuotaRepositoryResult<QuotaReservationRow>.Failed(
                QuotaLedgerFailure.None));
    }

    [Theory]
    [InlineData((int)QuotaLedgerFailure.ValidationFailed, "validation_failed", null)]
    [InlineData((int)QuotaLedgerFailure.GroupDisabled, "group_disabled", null)]
    [InlineData((int)QuotaLedgerFailure.QuotaExhausted, "group_quota_exhausted", null)]
    [InlineData((int)QuotaLedgerFailure.QuotaInsufficient, "group_quota_insufficient", null)]
    [InlineData((int)QuotaLedgerFailure.QuotaReserved, "group_quota_reserved", 1L)]
    [InlineData((int)QuotaLedgerFailure.InvalidApiKey, "invalid_api_key", null)]
    [InlineData((int)QuotaLedgerFailure.SubscriptionInactive, "subscription_inactive", null)]
    [InlineData((int)QuotaLedgerFailure.NoAvailableAccount, "no_available_account", 1L)]
    [InlineData((int)QuotaLedgerFailure.ResourceNotFound, "resource_not_found", null)]
    [InlineData((int)QuotaLedgerFailure.ResourceConflict, "resource_conflict", null)]
    [InlineData((int)QuotaLedgerFailure.IdempotencyConflict, "idempotency_conflict", null)]
    [InlineData((int)QuotaLedgerFailure.ReservationLeaseLost, "reservation_lease_lost", 1L)]
    [InlineData((int)QuotaLedgerFailure.TokenNumericOverflow, "token_numeric_overflow", null)]
    [InlineData((int)QuotaLedgerFailure.DependencyUnavailable, "dependency_unavailable", 1L)]
    [InlineData((int)QuotaLedgerFailure.Internal, "internal_error", null)]
    [InlineData(int.MaxValue, "internal_error", null)]
    public async Task ServiceMapsEveryRepositoryFailureWithoutCommitting(
        int failureValue,
        string expectedCode,
        long? expectedRetryAfterSeconds)
    {
        StubQuotaLedgerRepository repository = new()
        {
            ReserveResult = QuotaRepositoryResult<QuotaReservationRow>.Failed(
                (QuotaLedgerFailure)failureValue),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

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
    public async Task MarkDispatchSuccessRefreshesLeaseAndCommitsOneUnitOfWork()
    {
        DateTimeOffset refreshedLease = Now.AddMinutes(4);
        DateTimeOffset refreshedMaximum = Now.AddMinutes(9);
        StubQuotaLedgerRepository repository = new()
        {
            DispatchResult = QuotaRepositoryResult<QuotaDispatchRow>.Success(new(
                ReservationId,
                PeriodId,
                ReservationStatus.Pending,
                Now.AddSeconds(1),
                refreshedLease,
                refreshedMaximum)),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

        Result<DispatchedReservationHandle> result = await service.MarkDispatchedAsync(
            DispatchCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(refreshedLease, result.Value.Reservation.LeaseExpiresAt);
        Assert.Equal(refreshedMaximum, result.Value.Reservation.MaxExpiresAt);
        Assert.Equal(Now.AddSeconds(1), result.Value.DispatchStartedAt);
        Assert.Equal(SettlementProvider.OpenAi, result.Value.Provider);
        Assert.Equal("gpt-validation", result.Value.Model);
        Assert.Equal("quota:dispatch:v1:" + AttemptId.Value.ToString("N"),
            repository.LastDispatch?.Mutation.IdempotencyKey);
        Assert.Same(units.LastContext, repository.LastDispatchContext);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
    }

    [Fact]
    public async Task InvalidReleaseStopsBeforeUnitOfWorkAndRepositoryFailureRollsBack()
    {
        StubQuotaLedgerRepository repository = new()
        {
            ReleaseResult = QuotaRepositoryResult<QuotaTransitionRow>.Failed(
                QuotaLedgerFailure.ResourceConflict),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

        Result<QuotaTransitionResult> invalid = await service.ReleaseAsync(
            ReleaseCommand() with { Reason = " " },
            TestContext.Current.CancellationToken);
        Result<QuotaTransitionResult> failed = await service.ReleaseAsync(
            ReleaseCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(invalid.IsFailure);
        Assert.Equal("internal_error", invalid.Error.Code);
        Assert.True(failed.IsFailure);
        Assert.Equal("resource_conflict", failed.Error.Code);
        Assert.Equal(1, repository.ReleaseCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Same(units.LastContext, repository.LastReleaseContext);
    }

    [Fact]
    public async Task AdjustmentSuccessMapsEveryCounterAndCommitsOnce()
    {
        StubQuotaLedgerRepository repository = new()
        {
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Success(new(
                ReservationId,
                PeriodId,
                ReservationStatus.Settled,
                PreviousTokens: 130,
                CorrectedTokens: 155,
                DeltaTokens: 25,
                ConsumedTokens: 155,
                ReservedTokens: 0)),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

        Result<UsageAdjustmentResult> result = await service.AdjustAsync(
            AdjustmentCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(ReservationId, result.Value.ReservationId);
        Assert.Equal(AttemptId, result.Value.AttemptId);
        Assert.Equal(GroupId, result.Value.GroupId);
        Assert.Equal(PeriodId, result.Value.PeriodId);
        Assert.Equal(ReservationStatus.Settled, result.Value.ReservationStatus);
        Assert.Equal(new BigInteger(130), result.Value.PreviousTokens);
        Assert.Equal(new BigInteger(155), result.Value.CorrectedTokens);
        Assert.Equal(new BigInteger(25), result.Value.DeltaTokens);
        Assert.Equal(new BigInteger(155), result.Value.ConsumedTokens);
        Assert.Equal(BigInteger.Zero, result.Value.ReservedTokens);
        Assert.Equal(
            $"quota:adjust:v1:{AttemptId.Value:N}",
            repository.LastAdjustment?.Mutation.IdempotencyKey);
        Assert.Same(units.LastContext, repository.LastAdjustmentContext);
        Assert.Equal(1, repository.AdjustmentCalls);
        Assert.Equal(1, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
    }

    [Fact]
    public async Task InvalidAndOverflowAdjustmentsNeverWriteAndOverflowRaisesP0()
    {
        StubQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new();
        GroupQuotaLedgerService service = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        Result<UsageAdjustmentResult> invalid = await service.AdjustAsync(
            AdjustmentCommand() with { Reason = "" },
            TestContext.Current.CancellationToken);
        Result<UsageAdjustmentResult> overflow = await service.AdjustAsync(
            AdjustmentCommand() with
            {
                CorrectedUsage = new TokenUsage(
                    QuotaLedgerValidation.MaximumNumeric78,
                    BigInteger.One,
                    BigInteger.Zero,
                    BigInteger.Zero,
                    BigInteger.Zero),
            },
            TestContext.Current.CancellationToken);

        Assert.True(invalid.IsFailure);
        Assert.Equal("internal_error", invalid.Error.Code);
        Assert.True(overflow.IsFailure);
        Assert.Equal("token_numeric_overflow", overflow.Error.Code);
        Assert.Equal(0, repository.AdjustmentCalls);
        Assert.Equal(0, units.BeginCalls);
        Assert.Equal(1, events.Calls);
        Assert.Equal("group_quota.token_numeric_overflow", events.EventName);
        Assert.Equal("P0", events.Payload.GetProperty("severity").GetString());
        Assert.Equal("adjust_usage", events.Payload.GetProperty("operation").GetString());
        Assert.Equal(AttemptId.Value, events.Payload.GetProperty("attempt_id").GetGuid());
        Assert.Equal(CancellationToken.None, events.CancellationToken);
    }

    [Fact]
    public async Task DatabaseAdjustmentOverflowRollsBackAndRaisesP0()
    {
        StubQuotaLedgerRepository repository = new()
        {
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.TokenNumericOverflow),
        };
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new();
        GroupQuotaLedgerService service = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        Result<UsageAdjustmentResult> result = await service.AdjustAsync(
            AdjustmentCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("token_numeric_overflow", result.Error.Code);
        Assert.Equal(1, repository.AdjustmentCalls);
        Assert.Equal(1, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Equal(1, events.Calls);
        Assert.Equal("adjust_usage", events.Payload.GetProperty("operation").GetString());
    }

    [Fact]
    public async Task NonOverflowAdjustmentFailureDoesNotEmitAP0Alert()
    {
        StubQuotaLedgerRepository repository = new()
        {
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.IdempotencyConflict),
        };
        RecordingUnitOfWorkFactory units = new();
        RecordingOperationalEventWriter events = new();
        GroupQuotaLedgerService service = new(repository, units, events, NoOpIdempotentAuditAppender.Instance);

        Result<UsageAdjustmentResult> result = await service.AdjustAsync(
            AdjustmentCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("idempotency_conflict", result.Error.Code);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(1, units.DisposeCalls);
        Assert.Equal(0, events.Calls);
    }

    [Fact]
    public async Task FactReaderRejectsInvalidIdentityAndReportsMissingFact()
    {
        StubQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);
        CallerUnitOfWorkContext context = new();

        Result<AttemptSettlementFact> invalid = await service.GetByAttemptIdAsync(
            NonVersion7,
            context,
            TestContext.Current.CancellationToken);
        Result<AttemptSettlementFact> missing = await service.GetByAttemptIdAsync(
            AttemptId,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(invalid.IsFailure);
        Assert.Equal("validation_failed", invalid.Error.Code);
        Assert.True(missing.IsFailure);
        Assert.Equal("resource_not_found", missing.Error.Code);
        Assert.Equal(1, repository.FactCalls);
        Assert.Equal(AttemptId, repository.LastFactAttemptId);
        Assert.Same(context, repository.LastFactContext);
        Assert.Equal(0, units.BeginCalls);
    }

    [Fact]
    public async Task NullDependenciesCommandsAndFactContextFailFast()
    {
        StubQuotaLedgerRepository repository = new();
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

        Assert.Throws<ArgumentNullException>(
            () => new GroupQuotaLedgerService(null!, units, NoOpIdempotentAuditAppender.Instance));
        Assert.Throws<ArgumentNullException>(
            () => new GroupQuotaLedgerService(repository, null!, NoOpIdempotentAuditAppender.Instance));
        ArgumentNullException auditException = Assert.Throws<ArgumentNullException>(
            () => new GroupQuotaLedgerService(
                repository,
                units,
                idempotentAuditAppender: null!));
        Assert.Equal("idempotentAuditAppender", auditException.ParamName);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.ReserveAsync(null!, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.MarkDispatchedAsync(null!, CancellationToken.None)
                .ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.RenewAsync(null!, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.SettleAsync(null!, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.ReleaseAsync(null!, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.AdjustAsync(null!, CancellationToken.None).ConfigureAwait(false));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await service.GetByAttemptIdAsync(
                AttemptId,
                null!,
                CancellationToken.None).ConfigureAwait(false));

        Assert.Equal(0, units.BeginCalls);
        Assert.Equal(0, repository.TotalMutationCalls);
        Assert.Equal(0, repository.FactCalls);
    }

    [Fact]
    public async Task MissingOperationalWriterFailsClosedOnlyWhenAnAlertIsRequired()
    {
        StubQuotaLedgerRepository repository = new()
        {
            ReserveResult = QuotaRepositoryResult<QuotaReservationRow>.Failed(
                QuotaLedgerFailure.DependencyUnavailable),
            AdjustmentResult = QuotaRepositoryResult<UsageAdjustmentRow>.Failed(
                QuotaLedgerFailure.UsageWithoutDispatch),
        };
        RecordingUnitOfWorkFactory units = new();
        GroupQuotaLedgerService service = new(repository, units, NoOpIdempotentAuditAppender.Instance);

        Result<ReserveQuotaResult> ordinaryFailure = await service.ReserveAsync(
            ReserveCommand(),
            TestContext.Current.CancellationToken);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.SettleAsync(
                SettlementCommand() with
                {
                    Usage = new TokenUsage(
                        QuotaLedgerValidation.MaximumNumeric78,
                        BigInteger.One,
                        BigInteger.Zero,
                        BigInteger.Zero,
                        BigInteger.Zero),
                },
                TestContext.Current.CancellationToken).ConfigureAwait(false));
        InvalidOperationException adjustmentException =
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.AdjustAsync(
                    AdjustmentCommand(),
                    TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.True(ordinaryFailure.IsFailure);
        Assert.Equal("dependency_unavailable", ordinaryFailure.Error.Code);
        Assert.Contains("operational event writer", exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("operational event writer", adjustmentException.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, repository.ReserveCalls);
        Assert.Equal(0, repository.SettleCalls);
        Assert.Equal(1, repository.AdjustmentCalls);
        Assert.Equal(2, units.BeginCalls);
        Assert.Equal(0, units.CommitCalls);
        Assert.Equal(2, units.DisposeCalls);
    }

    private static ReserveQuotaCommand ReserveCommand() => new(
        RequestId,
        AttemptId,
        AttemptIndex: 0,
        UserId,
        ApiKeyId,
        SubscriptionId,
        GroupId,
        AccountId,
        ChannelId,
        UsageRequestEndpoint.Responses,
        RequestedModel: "gpt-validation",
        ClientRequestId: "client-validation",
        EstimatedTokens: 100,
        IsStreaming: false,
        LeaseOwner: "gateway-validation");

    private static ReservationHandle Reservation() => new(
        ReservationId,
        RequestId,
        AttemptId,
        AttemptIndex: 0,
        GroupId,
        PeriodId,
        AccountId,
        ChannelId,
        EstimatedTokens: 100,
        IsStreaming: false,
        LeaseOwner: "gateway-validation",
        LeaseExpiresAt: Now.AddMinutes(5),
        MaxExpiresAt: Now.AddMinutes(10));

    private static MarkReservationDispatchedCommand DispatchCommand() => new(
        Reservation(),
        SettlementProvider.OpenAi,
        "gpt-validation",
        new TokenEstimateSplit(60, 40));

    private static DispatchedReservationHandle DispatchedReservation() => new(
        ReservationStatus.Pending,
        Reservation(),
        SettlementProvider.OpenAi,
        "gpt-validation",
        new TokenEstimateSplit(60, 40),
        DispatchStartedAt: Now);

    private static SettleReservationCommand SettlementCommand() => new(
        DispatchedReservation(),
        UsageAttemptOutcome.Succeeded,
        UpstreamHttpStatus: 200,
        ErrorCode: null,
        UpstreamRequestId: "upstream-validation",
        FirstTokenAt: Now.AddSeconds(1),
        CompletedAt: Now.AddSeconds(2),
        RequestOutcome: UsageRequestOutcome.Succeeded,
        Usage: new TokenUsage(80, 50, 20, 10, 15),
        UsageSource: SettlementUsageSource.Upstream,
        RawUpstreamUsage: JsonSerializer.SerializeToElement(new
        {
            input_tokens = 80,
            output_tokens = 50,
        }));

    private static SettleReservationCommand ConfirmedNoExecutionSettlement() => new(
        DispatchedReservation(),
        UsageAttemptOutcome.Failed,
        UpstreamHttpStatus: 401,
        ErrorCode: "invalid_api_key",
        UpstreamRequestId: null,
        FirstTokenAt: null,
        CompletedAt: Now.AddSeconds(2),
        RequestOutcome: UsageRequestOutcome.Failed,
        Usage: Usage(0, 0),
        UsageSource: SettlementUsageSource.ConfirmedNoExecution,
        RawUpstreamUsage: null);

    private static ReleaseReservationCommand ReleaseCommand() => new(
        Reservation(),
        "upstream_not_started");

    private static AdjustAttemptUsageCommand AdjustmentCommand() => new(
        GroupId,
        AttemptId,
        AccountId,
        ChannelId,
        SettlementProvider.OpenAi,
        "gpt-validation",
        UsageAttemptOutcome.Succeeded,
        UpstreamHttpStatus: 200,
        ErrorCode: null,
        UpstreamRequestId: "upstream-validation",
        DispatchStartedAt: Now,
        FirstTokenAt: Now.AddSeconds(1),
        CompletedAt: Now.AddSeconds(2),
        RequestOutcome: UsageRequestOutcome.Succeeded,
        CorrectedUsage: new TokenUsage(90, 65, 20, 10, 15),
        UsageSource: SettlementUsageSource.Upstream,
        RawUpstreamUsage: JsonSerializer.SerializeToElement(new
        {
            input_tokens = 90,
            output_tokens = 65,
        }),
        Reason: "authoritative upstream usage");

    private static TokenUsage Usage(long input, long output) => new(
        input,
        output,
        CacheReadTokens: 0,
        CacheCreationTokens: 0,
        ThinkingTokens: 0);

    private static (string Label, TokenUsage Usage)[] InvalidTokenUsages() =>
    [
        ("negative input", new TokenUsage(-1, 0, 0, 0, 0)),
        ("negative output", new TokenUsage(0, -1, 0, 0, 0)),
        ("negative cache read", new TokenUsage(0, 0, -1, 0, 0)),
        ("negative cache creation", new TokenUsage(0, 0, 0, -1, 0)),
        ("negative thinking", new TokenUsage(0, 0, 0, 0, -1)),
        ("cache read above input", new TokenUsage(1, 0, 2, 0, 0)),
        ("cache creation above input", new TokenUsage(1, 0, 0, 2, 0)),
        ("combined cache above input", new TokenUsage(2, 0, 2, 1, 0)),
        ("thinking above output", new TokenUsage(0, 1, 0, 0, 2)),
    ];

    private static QuotaReservationRow ReservationRow() => new(
        ReservationId,
        PeriodId,
        ReservationStatus.Pending,
        TotalTokens: 1_000,
        ConsumedTokens: 100,
        ReservedTokens: 100,
        RemainingTokens: 800,
        LeaseExpiresAt: Now.AddMinutes(5),
        MaxExpiresAt: Now.AddMinutes(10));

    private static EntityId Id(int suffix) => new(
        Guid.Parse($"018f3a4b-5c6d-7e8f-9123-{suffix:D12}"));

    private sealed class StubQuotaLedgerRepository : IQuotaLedgerRepository
    {
        internal QuotaRepositoryResult<QuotaReservationRow>? ReserveResult { get; init; }

        internal QuotaRepositoryResult<QuotaDispatchRow>? DispatchResult { get; init; }

        internal QuotaRepositoryResult<QuotaTransitionRow>? SettleResult { get; init; }

        internal QuotaRepositoryResult<QuotaTransitionRow>? ReleaseResult { get; init; }

        internal QuotaRepositoryResult<UsageAdjustmentRow>? AdjustmentResult { get; init; }

        internal AttemptSettlementFact? Fact { get; init; }

        internal int ReserveCalls { get; private set; }

        internal int DispatchCalls { get; private set; }

        internal int SettleCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal int AdjustmentCalls { get; private set; }

        internal int FactCalls { get; private set; }

        internal int TotalMutationCalls => ReserveCalls + DispatchCalls + SettleCalls
            + ReleaseCalls + AdjustmentCalls;

        internal MarkReservationDispatchedWrite? LastDispatch { get; private set; }

        internal AdjustAttemptUsageWrite? LastAdjustment { get; private set; }

        internal IUnitOfWorkContext? LastDispatchContext { get; private set; }

        internal IUnitOfWorkContext? LastReleaseContext { get; private set; }

        internal IUnitOfWorkContext? LastAdjustmentContext { get; private set; }

        internal EntityId? LastFactAttemptId { get; private set; }

        internal IUnitOfWorkContext? LastFactContext { get; private set; }

        public ValueTask<QuotaRepositoryResult<QuotaReservationRow>> ReserveAsync(
            ReserveQuotaWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReserveCalls++;
            return ValueTask.FromResult(
                ReserveResult ?? throw Unexpected(nameof(ReserveAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaDispatchRow>> MarkDispatchedAsync(
            MarkReservationDispatchedWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DispatchCalls++;
            LastDispatch = write;
            LastDispatchContext = unitOfWorkContext;
            return ValueTask.FromResult(
                DispatchResult ?? throw Unexpected(nameof(MarkDispatchedAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaRenewalRow>> RenewAsync(
            RenewReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected(nameof(RenewAsync));

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> SettleAsync(
            SettleReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SettleCalls++;
            return ValueTask.FromResult(
                SettleResult ?? throw Unexpected(nameof(SettleAsync)));
        }

        public ValueTask<QuotaRepositoryResult<QuotaTransitionRow>> ReleaseAsync(
            ReleaseReservationWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReleaseCalls++;
            LastReleaseContext = unitOfWorkContext;
            return ValueTask.FromResult(
                ReleaseResult ?? throw Unexpected(nameof(ReleaseAsync)));
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
            LastAdjustmentContext = unitOfWorkContext;
            return ValueTask.FromResult(
                AdjustmentResult ?? throw Unexpected(nameof(AdjustUsageAsync)));
        }

        public ValueTask<AttemptSettlementFact?> GetAttemptSettlementFactAsync(
            EntityId attemptId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FactCalls++;
            LastFactAttemptId = attemptId;
            LastFactContext = unitOfWorkContext;
            return ValueTask.FromResult(Fact);
        }

        private static InvalidOperationException Unexpected(string operation) => new(
            $"The {operation} repository result was not configured.");
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal IUnitOfWorkContext? LastContext { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            RecordingContext context = new();
            LastContext = context;
            return ValueTask.FromResult<IUnitOfWork>(new RecordingUnitOfWork(
                this,
                context));
        }

        private sealed class RecordingUnitOfWork(
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

        private sealed class RecordingContext : IUnitOfWorkContext;
    }

    private sealed class CallerUnitOfWorkContext : IUnitOfWorkContext;

    private sealed class RecordingOperationalEventWriter : IOperationalEventWriter
    {
        internal int Calls { get; private set; }

        internal string? EventName { get; private set; }

        internal JsonElement Payload { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public ValueTask WriteAsync(
            string eventName,
            JsonElement payload,
            CancellationToken cancellationToken)
        {
            Calls++;
            EventName = eventName;
            Payload = payload;
            CancellationToken = cancellationToken;
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
