#pragma warning disable MA0051 // Signed PostgreSQL row mutations stay explicit in contract tests.
using System.Numerics;
using Npgsql;
using NpgsqlTypes;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

namespace PoolAI.IntegrationTests;

[Collection(PostgresRuntimeTestGroup.Name)]
public sealed class PostgresQuotaLedgerAbiContractTests(PostgresRuntimeFixture fixture)
{
    private static readonly DateTimeOffset ContractTime = new(
        2030,
        1,
        2,
        3,
        4,
        5,
        TimeSpan.Zero);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReservationReaderRejectsEveryMalformedSignedPosition()
    {
        // Governing contract: migration 0015 signs one reserve row and the
        // non-negative, internally consistent quota-position tuple.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ReservationShape valid = ValidReservation();
        ReserveQuotaWrite write = ReserveWrite(valid.ReservationId);

        QuotaReservationRow row = await ReadReservationAsync(
            valid,
            write,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(valid.ReservationId, row.ReservationId);

        await AssertInvalidReservationAsync(valid, write, 0, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidReservationAsync(valid, write, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { ReservationId = EntityId.New() },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { MaxExpiresAt = valid.LeaseExpiresAt.AddTicks(-10) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { PeriodId = new EntityId(Guid.NewGuid()) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { TotalTokens = BigInteger.Zero, RemainingTokens = BigInteger.Zero },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with
            {
                TotalTokens = QuotaLedgerValidation.MaximumSafeTokenCount + BigInteger.One,
                ConsumedTokens = BigInteger.Zero,
                ReservedTokens = BigInteger.Zero,
                RemainingTokens = QuotaLedgerValidation.MaximumSafeTokenCount + BigInteger.One,
            },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { ConsumedTokens = -BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with
            {
                ConsumedTokens = QuotaLedgerValidation.MaximumNumeric78 + BigInteger.One,
                RemainingTokens = BigInteger.Zero,
            },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { ReservedTokens = -BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with
            {
                ReservedTokens = QuotaLedgerValidation.MaximumNumeric78 + BigInteger.One,
                RemainingTokens = BigInteger.Zero,
            },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { RemainingTokens = valid.RemainingTokens + BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidReservationAsync(
            valid with { Status = "unknown" },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task DispatchAndTransitionReadersRejectMalformedSignedIdentityAndTime()
    {
        // Governing contract: migration 0015 signs exactly one row whose
        // reservation/period identity and terminal transition are immutable.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DispatchShape dispatch = ValidDispatch();
        MarkReservationDispatchedWrite dispatchWrite = DispatchWrite(dispatch);
        QuotaDispatchRow dispatchRow = await ReadDispatchAsync(
            dispatch,
            dispatchWrite,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ReservationStatus.Pending, dispatchRow.Status);

        await AssertInvalidDispatchAsync(dispatch, dispatchWrite, 0, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidDispatchAsync(dispatch, dispatchWrite, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidDispatchAsync(
            dispatch with { ReservationId = EntityId.New() },
            dispatchWrite,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidDispatchAsync(
            dispatch with { PeriodId = EntityId.New() },
            dispatchWrite,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidDispatchAsync(
            dispatch with { DispatchStartedAt = DateTimeOffset.UnixEpoch.AddTicks(-10) },
            dispatchWrite,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidDispatchAsync(
            dispatch with { MaxExpiresAt = dispatch.LeaseExpiresAt.AddTicks(-10) },
            dispatchWrite,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidDispatchAsync(
            dispatch with { Status = "unknown" },
            dispatchWrite,
            1,
            cancellationToken).ConfigureAwait(true);

        TransitionShape transition = ValidTransition();
        QuotaTransitionRow transitionRow = await ReadTransitionAsync(
            transition,
            transition.ReservationId,
            transition.PeriodId,
            ReservationStatus.Settled,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ReservationStatus.Settled, transitionRow.Status);

        await AssertInvalidTransitionAsync(transition, 0, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidTransitionAsync(transition, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidTransitionAsync(
            transition,
            1,
            cancellationToken,
            expectedReservationId: EntityId.New()).ConfigureAwait(true);
        await AssertInvalidTransitionAsync(
            transition,
            1,
            cancellationToken,
            expectedPeriodId: EntityId.New()).ConfigureAwait(true);
        await AssertInvalidTransitionAsync(
            transition,
            1,
            cancellationToken,
            expectedStatus: ReservationStatus.Released).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AdjustmentReaderRejectsEveryMalformedSignedNumericTuple()
    {
        // Governing contract: migration 0015 signs settled/expired correction
        // rows and the exact previous/corrected/delta arithmetic.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AdjustmentShape valid = ValidAdjustment();
        AdjustAttemptUsageWrite write = AdjustmentWrite(valid.CorrectedTokens);

        UsageAdjustmentRow settled = await ReadAdjustmentAsync(
            valid,
            write,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ReservationStatus.Settled, settled.ReservationStatus);
        UsageAdjustmentRow expired = await ReadAdjustmentAsync(
            valid with { Status = "expired" },
            write,
            rowCount: 1,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(ReservationStatus.Expired, expired.ReservationStatus);

        await AssertInvalidAdjustmentAsync(valid, write, 0, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(valid, write, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { ReservationId = new EntityId(Guid.NewGuid()) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { PeriodId = new EntityId(Guid.NewGuid()) },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { Status = "pending" },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { PreviousTokens = -BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with
            {
                PreviousTokens = QuotaLedgerValidation.MaximumNumeric78 + BigInteger.One,
            },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid,
            AdjustmentWrite(valid.CorrectedTokens + BigInteger.One),
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with
            {
                CorrectedTokens = -BigInteger.One,
                DeltaTokens = -BigInteger.One - valid.PreviousTokens,
            },
            AdjustmentWrite(-BigInteger.One),
            1,
            cancellationToken).ConfigureAwait(true);
        BigInteger overflow = QuotaLedgerValidation.MaximumNumeric78 + BigInteger.One;
        await AssertInvalidAdjustmentAsync(
            valid with
            {
                CorrectedTokens = overflow,
                DeltaTokens = overflow - valid.PreviousTokens,
            },
            AdjustmentWrite(overflow),
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { DeltaTokens = valid.DeltaTokens + BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { ConsumedTokens = -BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { ConsumedTokens = overflow },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { ReservedTokens = -BigInteger.One },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidAdjustmentAsync(
            valid with { ReservedTokens = overflow },
            write,
            1,
            cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AttemptFactReaderAcceptsEverySignedVocabularyVariant()
    {
        // Governing contract: DEC-015 and migration 0015 sign provider,
        // outcome, usage-source, streaming, timestamp, and correction facts.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttemptFactShape valid = ValidFact();

        Assert.Null(await ReadFactAsync(valid, valid.AttemptId, 0, cancellationToken)
            .ConfigureAwait(true));
        AttemptSettlementFact fact = Assert.IsType<AttemptSettlementFact>(
            await ReadFactAsync(valid, valid.AttemptId, 1, cancellationToken)
                .ConfigureAwait(true));
        Assert.Equal(valid.AttemptId, fact.AttemptId);

        AttemptFactShape[] variants =
        [
            valid with { Provider = "openai_compatible" },
            valid with { Outcome = "failed" },
            valid with { Outcome = "cancelled" },
            valid with { UsageSource = "local_tokenizer", IsEstimated = true },
            valid with { UsageSource = "conservative_estimate", IsEstimated = true },
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = "upstream_denied",
                UpstreamHttpStatus = null,
            },
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = "upstream_denied",
                UpstreamHttpStatus = 401,
            },
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = "upstream_denied",
                UpstreamHttpStatus = 403,
            },
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = "upstream_throttled",
                UpstreamHttpStatus = 429,
            },
            valid with
            {
                Adjustment = new AdjustmentFactShape(
                    EntityId.New(),
                    valid.Tokens.TotalTokens,
                    new TokenUsage(9, 5, 0, 0, 0),
                    "local_tokenizer",
                    BigInteger.One,
                    valid.CompletedAt.AddTicks(10)),
            },
        ];

        foreach (AttemptFactShape variant in variants)
        {
            Assert.NotNull(await ReadFactAsync(
                variant,
                variant.AttemptId,
                1,
                cancellationToken).ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AttemptFactReaderRejectsMalformedIdentityVocabularyAndTiming()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AttemptFactShape valid = ValidFact();
        AdjustmentFactShape validAdjustment = new(
            EntityId.New(),
            valid.Tokens.TotalTokens,
            new TokenUsage(9, 5, 0, 0, 0),
            "upstream",
            BigInteger.One,
            valid.CompletedAt.AddTicks(10));

        await AssertInvalidFactAsync(valid, valid.AttemptId, 2, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { RoutingGroupId = EntityId.New() },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { ReservationIsStreaming = false },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(valid, EntityId.New(), 1, cancellationToken)
            .ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { AttemptIndex = -1 },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { RequestedModel = " " },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UpstreamModel = " " },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UpstreamHttpStatus = 99 },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UpstreamHttpStatus = 600 },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { ErrorCode = " " },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { Tokens = valid.Tokens with { InputTokens = -BigInteger.One } },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { IsEstimated = true },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UsageSource = "local_tokenizer" },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UsageSource = "conservative_estimate" },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                UsageSource = "confirmed_no_execution",
                IsEstimated = true,
                ErrorCode = "denied",
                UpstreamHttpStatus = 401,
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { CompletedAt = valid.DispatchStartedAt.AddTicks(-10) },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { FirstTokenAt = valid.DispatchStartedAt.AddTicks(-10) },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { FirstTokenAt = valid.CompletedAt.AddTicks(10) },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = null,
                UpstreamHttpStatus = 401,
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                UsageSource = "confirmed_no_execution",
                ErrorCode = "denied",
                UpstreamHttpStatus = 400,
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { Provider = "unknown" },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { Outcome = "unknown" },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with { UsageSource = "unknown" },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                Adjustment = validAdjustment with
                {
                    PreviousTotalTokens = valid.Tokens.TotalTokens + BigInteger.One,
                },
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                Adjustment = validAdjustment with
                {
                    CorrectedTokens = new TokenUsage(-1, 0, 0, 0, 0),
                },
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                Adjustment = validAdjustment with { DeltaTokens = new BigInteger(2) },
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                Adjustment = validAdjustment with
                {
                    AdjustedAt = valid.CompletedAt.AddTicks(-10),
                },
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
        await AssertInvalidFactAsync(
            valid with
            {
                Adjustment = validAdjustment with { UsageSource = "unknown" },
            },
            valid.AttemptId,
            1,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask<QuotaReservationRow> ReadReservationAsync(
        ReservationShape shape,
        ReserveQuotaWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = ReservationCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadReservationAsync(
            command,
            write,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidReservationAsync(
        ReservationShape shape,
        ReserveQuotaWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = ReservationCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadReservationAsync(
                command,
                write,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask<QuotaDispatchRow> ReadDispatchAsync(
        DispatchShape shape,
        MarkReservationDispatchedWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = DispatchCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadDispatchAsync(
            command,
            write,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidDispatchAsync(
        DispatchShape shape,
        MarkReservationDispatchedWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = DispatchCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadDispatchAsync(
                command,
                write,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask<QuotaTransitionRow> ReadTransitionAsync(
        TransitionShape shape,
        EntityId expectedReservationId,
        EntityId expectedPeriodId,
        ReservationStatus expectedStatus,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = TransitionCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadTransitionAsync(
            command,
            expectedReservationId,
            expectedPeriodId,
            expectedStatus,
            "test transition",
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidTransitionAsync(
        TransitionShape shape,
        int rowCount,
        CancellationToken cancellationToken,
        EntityId? expectedReservationId = null,
        EntityId? expectedPeriodId = null,
        ReservationStatus expectedStatus = ReservationStatus.Settled)
    {
        using NpgsqlCommand command = TransitionCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadTransitionAsync(
                command,
                expectedReservationId ?? shape.ReservationId,
                expectedPeriodId ?? shape.PeriodId,
                expectedStatus,
                "test transition",
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask<UsageAdjustmentRow> ReadAdjustmentAsync(
        AdjustmentShape shape,
        AdjustAttemptUsageWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdjustmentCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadAdjustmentAsync(
            command,
            write,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidAdjustmentAsync(
        AdjustmentShape shape,
        AdjustAttemptUsageWrite write,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AdjustmentCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadAdjustmentAsync(
                command,
                write,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async ValueTask<AttemptSettlementFact?> ReadFactAsync(
        AttemptFactShape shape,
        EntityId expectedAttemptId,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AttemptFactCommand(shape, rowCount);
        return await PostgresQuotaLedgerAbiContract.ReadAttemptFactAsync(
            command,
            expectedAttemptId,
            cancellationToken).ConfigureAwait(true);
    }

    private async ValueTask AssertInvalidFactAsync(
        AttemptFactShape shape,
        EntityId expectedAttemptId,
        int rowCount,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = AttemptFactCommand(shape, rowCount);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await PostgresQuotaLedgerAbiContract.ReadAttemptFactAsync(
                command,
                expectedAttemptId,
                cancellationToken).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private NpgsqlCommand ReservationCommand(ReservationShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid, $2::uuid, $3::text,
                $4::numeric, $5::numeric, $6::numeric, $7::numeric,
                $8::timestamptz, $9::timestamptz
            FROM generate_series(1, $10::integer);
            """);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddText(command, shape.Status);
        AddNumeric(command, shape.TotalTokens);
        AddNumeric(command, shape.ConsumedTokens);
        AddNumeric(command, shape.ReservedTokens);
        AddNumeric(command, shape.RemainingTokens);
        AddTimestamp(command, shape.LeaseExpiresAt);
        AddTimestamp(command, shape.MaxExpiresAt);
        AddInteger(command, rowCount);
        return command;
    }

    private NpgsqlCommand DispatchCommand(DispatchShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid, $2::uuid, $3::text,
                $4::timestamptz, $5::timestamptz, $6::timestamptz
            FROM generate_series(1, $7::integer);
            """);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddText(command, shape.Status);
        AddTimestamp(command, shape.DispatchStartedAt);
        AddTimestamp(command, shape.LeaseExpiresAt);
        AddTimestamp(command, shape.MaxExpiresAt);
        AddInteger(command, rowCount);
        return command;
    }

    private NpgsqlCommand TransitionCommand(TransitionShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid, $2::uuid, $3::text,
                $4::numeric, $5::numeric, $6::numeric, $7::numeric
            FROM generate_series(1, $8::integer);
            """);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddText(command, shape.Status);
        AddNumeric(command, shape.TotalTokens);
        AddNumeric(command, shape.ConsumedTokens);
        AddNumeric(command, shape.ReservedTokens);
        AddNumeric(command, shape.RemainingTokens);
        AddInteger(command, rowCount);
        return command;
    }

    private NpgsqlCommand AdjustmentCommand(AdjustmentShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid, $2::uuid, $3::text,
                $4::numeric, $5::numeric, $6::numeric, $7::numeric, $8::numeric
            FROM generate_series(1, $9::integer);
            """);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddText(command, shape.Status);
        AddNumeric(command, shape.PreviousTokens);
        AddNumeric(command, shape.CorrectedTokens);
        AddNumeric(command, shape.DeltaTokens);
        AddNumeric(command, shape.ConsumedTokens);
        AddNumeric(command, shape.ReservedTokens);
        AddInteger(command, rowCount);
        return command;
    }

    private NpgsqlCommand AttemptFactCommand(AttemptFactShape shape, int rowCount)
    {
        NpgsqlCommand command = fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                $1::uuid, $2::uuid, $3::integer, $4::uuid,
                $5::uuid, $6::uuid, $7::uuid, $8::uuid,
                $9::text, $10::text, $11::text, $12::uuid,
                $13::numeric, $14::numeric, $15::numeric, $16::numeric, $17::numeric,
                $18::text, $19::boolean, $20::timestamptz,
                $21::timestamptz, $22::timestamptz,
                $23::uuid, $24::numeric,
                $25::numeric, $26::numeric, $27::numeric, $28::numeric, $29::numeric,
                $30::text, $31::numeric, $32::timestamptz,
                $33::text, $34::boolean, $35::integer, $36::text, $37::boolean
            FROM generate_series(1, $38::integer);
            """);
        AddUuid(command, shape.AttemptId.Value);
        AddUuid(command, shape.RequestId.Value);
        AddInteger(command, shape.AttemptIndex);
        AddUuid(command, shape.ReservationId.Value);
        AddUuid(command, shape.GroupId.Value);
        AddUuid(command, shape.PeriodId.Value);
        AddUuid(command, shape.AccountId.Value);
        AddUuid(command, shape.ChannelId.Value);
        AddText(command, shape.Provider);
        AddText(command, shape.UpstreamModel);
        AddText(command, shape.Outcome);
        AddUuid(command, shape.RoutingGroupId.Value);
        AddUsage(command, shape.Tokens);
        AddText(command, shape.UsageSource);
        AddBoolean(command, shape.IsEstimated);
        AddTimestamp(command, shape.DispatchStartedAt);
        AddNullableTimestamp(command, shape.FirstTokenAt);
        AddTimestamp(command, shape.CompletedAt);
        AddAdjustment(command, shape.Adjustment);
        AddText(command, shape.RequestedModel);
        AddBoolean(command, shape.RequestIsStreaming);
        AddNullableInteger(command, shape.UpstreamHttpStatus);
        AddNullableText(command, shape.ErrorCode);
        AddBoolean(command, shape.ReservationIsStreaming);
        AddInteger(command, rowCount);
        return command;
    }

    private static void AddAdjustment(
        NpgsqlCommand command,
        AdjustmentFactShape? adjustment)
    {
        AddNullableUuid(command, adjustment?.QuotaEventId.Value);
        AddNullableNumeric(command, adjustment?.PreviousTotalTokens);
        AddNullableNumeric(command, adjustment?.CorrectedTokens.InputTokens);
        AddNullableNumeric(command, adjustment?.CorrectedTokens.OutputTokens);
        AddNullableNumeric(command, adjustment?.CorrectedTokens.CacheReadTokens);
        AddNullableNumeric(command, adjustment?.CorrectedTokens.CacheCreationTokens);
        AddNullableNumeric(command, adjustment?.CorrectedTokens.ThinkingTokens);
        AddNullableText(command, adjustment?.UsageSource);
        AddNullableNumeric(command, adjustment?.DeltaTokens);
        AddNullableTimestamp(command, adjustment?.AdjustedAt);
    }

    private static void AddUsage(NpgsqlCommand command, TokenUsage usage)
    {
        AddNumeric(command, usage.InputTokens);
        AddNumeric(command, usage.OutputTokens);
        AddNumeric(command, usage.CacheReadTokens);
        AddNumeric(command, usage.CacheCreationTokens);
        AddNumeric(command, usage.ThinkingTokens);
    }

    private static void AddUuid(NpgsqlCommand command, Guid value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, value);

    private static void AddNullableUuid(NpgsqlCommand command, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddText(NpgsqlCommand command, string value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Text, value);

    private static void AddNullableText(NpgsqlCommand command, string? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Text,
            Value = value is null ? DBNull.Value : value,
        });

    private static void AddInteger(NpgsqlCommand command, int value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, value);

    private static void AddNullableInteger(NpgsqlCommand command, int? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Integer,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddBoolean(NpgsqlCommand command, bool value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.Boolean, value);

    private static void AddNumeric(NpgsqlCommand command, BigInteger value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value,
        });

    private static void AddNullableNumeric(NpgsqlCommand command, BigInteger? value) =>
        command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Numeric,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddTimestamp(NpgsqlCommand command, DateTimeOffset value) =>
        command.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, value);

    private static void AddNullableTimestamp(
        NpgsqlCommand command,
        DateTimeOffset? value) => command.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static ReservationShape ValidReservation() => new(
        EntityId.New(),
        EntityId.New(),
        "pending",
        new BigInteger(100),
        new BigInteger(20),
        new BigInteger(30),
        new BigInteger(50),
        ContractTime.AddMinutes(1),
        ContractTime.AddMinutes(2));

    private static DispatchShape ValidDispatch() => new(
        EntityId.New(),
        EntityId.New(),
        "pending",
        ContractTime,
        ContractTime.AddMinutes(1),
        ContractTime.AddMinutes(2));

    private static TransitionShape ValidTransition() => new(
        EntityId.New(),
        EntityId.New(),
        "settled",
        new BigInteger(100),
        new BigInteger(40),
        BigInteger.Zero,
        new BigInteger(60));

    private static AdjustmentShape ValidAdjustment() => new(
        EntityId.New(),
        EntityId.New(),
        "settled",
        new BigInteger(10),
        new BigInteger(14),
        new BigInteger(4),
        new BigInteger(14),
        BigInteger.Zero);

    private static AttemptFactShape ValidFact()
    {
        EntityId groupId = EntityId.New();
        return new AttemptFactShape(
            EntityId.New(),
            EntityId.New(),
            0,
            EntityId.New(),
            groupId,
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            "openai",
            "gpt-contract",
            "gpt-upstream",
            "succeeded",
            groupId,
            new TokenUsage(8, 5, 0, 0, 0),
            "upstream",
            false,
            ContractTime,
            ContractTime.AddTicks(10),
            ContractTime.AddTicks(20),
            200,
            null,
            true,
            true,
            null);
    }

    private static ReserveQuotaWrite ReserveWrite(EntityId reservationId) => new(
        new ReserveQuotaCommand(
            EntityId.New(),
            EntityId.New(),
            0,
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            UsageRequestEndpoint.Responses,
            "gpt-contract",
            null,
            30,
            false,
            "abi-test"),
        reservationId,
        Mutation());

    private static MarkReservationDispatchedWrite DispatchWrite(DispatchShape shape) => new(
        new MarkReservationDispatchedCommand(
            new ReservationHandle(
                shape.ReservationId,
                EntityId.New(),
                EntityId.New(),
                0,
                EntityId.New(),
                shape.PeriodId,
                EntityId.New(),
                EntityId.New(),
                30,
                false,
                "abi-test",
                shape.LeaseExpiresAt,
                shape.MaxExpiresAt),
            SettlementProvider.OpenAi,
            "gpt-contract",
            new TokenEstimateSplit(20, 10)),
        Mutation());

    private static AdjustAttemptUsageWrite AdjustmentWrite(BigInteger correctedTokens) => new(
        new AdjustAttemptUsageCommand(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            SettlementProvider.OpenAi,
            "gpt-contract",
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            null,
            ContractTime,
            ContractTime.AddTicks(10),
            ContractTime.AddTicks(20),
            UsageRequestOutcome.Succeeded,
            new TokenUsage(correctedTokens, 0, 0, 0, 0),
            SettlementUsageSource.Upstream,
            null,
            "contract correction"),
        Mutation());

    private static QuotaMutationIdentity Mutation() => new(
        EntityId.New(),
        EntityId.New(),
        $"abi:{Guid.CreateVersion7():N}");

    private sealed record ReservationShape(
        EntityId ReservationId,
        EntityId PeriodId,
        string Status,
        BigInteger TotalTokens,
        BigInteger ConsumedTokens,
        BigInteger ReservedTokens,
        BigInteger RemainingTokens,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset MaxExpiresAt);

    private sealed record DispatchShape(
        EntityId ReservationId,
        EntityId PeriodId,
        string Status,
        DateTimeOffset DispatchStartedAt,
        DateTimeOffset LeaseExpiresAt,
        DateTimeOffset MaxExpiresAt);

    private sealed record TransitionShape(
        EntityId ReservationId,
        EntityId PeriodId,
        string Status,
        BigInteger TotalTokens,
        BigInteger ConsumedTokens,
        BigInteger ReservedTokens,
        BigInteger RemainingTokens);

    private sealed record AdjustmentShape(
        EntityId ReservationId,
        EntityId PeriodId,
        string Status,
        BigInteger PreviousTokens,
        BigInteger CorrectedTokens,
        BigInteger DeltaTokens,
        BigInteger ConsumedTokens,
        BigInteger ReservedTokens);

    private sealed record AttemptFactShape(
        EntityId AttemptId,
        EntityId RequestId,
        int AttemptIndex,
        EntityId ReservationId,
        EntityId GroupId,
        EntityId PeriodId,
        EntityId AccountId,
        EntityId ChannelId,
        string Provider,
        string RequestedModel,
        string UpstreamModel,
        string Outcome,
        EntityId RoutingGroupId,
        TokenUsage Tokens,
        string UsageSource,
        bool IsEstimated,
        DateTimeOffset DispatchStartedAt,
        DateTimeOffset? FirstTokenAt,
        DateTimeOffset CompletedAt,
        int? UpstreamHttpStatus,
        string? ErrorCode,
        bool RequestIsStreaming,
        bool ReservationIsStreaming,
        AdjustmentFactShape? Adjustment);

    private sealed record AdjustmentFactShape(
        EntityId QuotaEventId,
        BigInteger PreviousTotalTokens,
        TokenUsage CorrectedTokens,
        string UsageSource,
        BigInteger DeltaTokens,
        DateTimeOffset AdjustedAt);
}
#pragma warning restore MA0051
