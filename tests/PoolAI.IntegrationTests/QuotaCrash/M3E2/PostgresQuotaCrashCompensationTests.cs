#pragma warning disable MA0051 // Each integration test keeps its complete quota ledger evidence chain visible; this partial filename matches its xUnit class.
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PoolAI.BuildingBlocks;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;

namespace PoolAI.IntegrationTests;

public sealed partial class PostgresQuotaCrashCompensationTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProductionLedgerSettlesActualAboveEstimateAndReplaysExactly()
    {
        // Governing contracts: AC-013/014/031 and database README sections 5-6.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        CrashAttempt attempt = scenario.PreDispatch;
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        ReserveQuotaCommand reserveCommand = Command(scenario, attempt);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserved);
        Assert.Equal(ReservationStatus.Pending, reserved.Value.Status);
        Assert.Equal(new BigInteger(120), reserved.Value.Quota.ReservedTokens);

        Result<ReserveQuotaResult> reserveReplay = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(reserveReplay);
        Assert.Equal(reserved.Value, reserveReplay.Value);

        MarkReservationDispatchedCommand markCommand = new(
            reserved.Value.Reservation,
            SettlementProvider.OpenAi,
            Model,
            new TokenEstimateSplit(80, 40));
        Result<DispatchedReservationHandle> dispatched = await ledger
            .MarkDispatchedAsync(markCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(dispatched);
        Assert.Equal(ReservationStatus.Pending, dispatched.Value.Status);

        Result<DispatchedReservationHandle> dispatchReplay = await ledger
            .MarkDispatchedAsync(markCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(dispatchReplay);
        Assert.Equal(dispatched.Value, dispatchReplay.Value);

        DateTimeOffset firstTokenAt = dispatched.Value.DispatchStartedAt.AddMilliseconds(1);
        DateTimeOffset completedAt = firstTokenAt.AddMilliseconds(1);
        SettleReservationCommand settleCommand = new(
            dispatched.Value,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            "upstream-request-ac13",
            firstTokenAt,
            completedAt,
            UsageRequestOutcome.Succeeded,
            new TokenUsage(100, 30, 10, 5, 4),
            SettlementUsageSource.Upstream,
            JsonSerializer.SerializeToElement(new { input_tokens = 100, output_tokens = 30 }));
        Result<QuotaTransitionResult> settled = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(settled);
        Assert.Equal(ReservationStatus.Settled, settled.Value.Status);
        Assert.Equal(new BigInteger(130), settled.Value.Quota.ConsumedTokens);
        Assert.Equal(BigInteger.Zero, settled.Value.Quota.ReservedTokens);

        Result<QuotaTransitionResult> settleReplay = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(settleReplay);
        Assert.Equal(settled.Value, settleReplay.Value);

        LedgerEvidence beforeAdjustment = await ReadLedgerEvidenceAsync(
            scenario,
            attempt,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("settled", beforeAdjustment.ReservationStatus);
        Assert.Equal("130", beforeAdjustment.ActualTokens);
        Assert.Equal("upstream", beforeAdjustment.UsageSource);
        Assert.Equal("130", beforeAdjustment.ConsumedTokens);
        Assert.Equal("0", beforeAdjustment.ReservedTokens);
        Assert.Equal("succeeded", beforeAdjustment.RequestStatus);
        Assert.Equal(1, beforeAdjustment.RequestAttemptCount);
        Assert.Equal(1, beforeAdjustment.AttemptCount);
        Assert.Equal(3, beforeAdjustment.EventCount);
        Assert.Equal(3, beforeAdjustment.EventOutboxCount);

        IUsageAdjustmentWriter adjustmentWriter =
            _fixture.WorkerServices.GetRequiredService<IUsageAdjustmentWriter>();
        AdjustAttemptUsageCommand adjustCommand = new(
            scenario.GroupId.AsEntityId(),
            attempt.AttemptId.AsEntityId(),
            scenario.AccountId.AsEntityId(),
            scenario.ChannelId.AsEntityId(),
            SettlementProvider.OpenAi,
            Model,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            "upstream-request-ac13",
            dispatched.Value.DispatchStartedAt,
            firstTokenAt,
            completedAt,
            UsageRequestOutcome.Succeeded,
            new TokenUsage(120, 35, 10, 5, 4),
            SettlementUsageSource.Upstream,
            JsonSerializer.SerializeToElement(new { input_tokens = 120, output_tokens = 35 }),
            "late authoritative usage");
        Result<UsageAdjustmentResult> adjusted = await adjustmentWriter
            .AdjustAsync(adjustCommand, cancellationToken)
            .ConfigureAwait(true);
        Assert.True(adjusted.IsSuccess, adjusted.IsFailure ? adjusted.Error.Code : string.Empty);
        Assert.Equal(new BigInteger(130), adjusted.Value.PreviousTokens);
        Assert.Equal(new BigInteger(155), adjusted.Value.CorrectedTokens);
        Assert.Equal(new BigInteger(25), adjusted.Value.DeltaTokens);

        Result<UsageAdjustmentResult> adjustmentReplay = await adjustmentWriter
            .AdjustAsync(adjustCommand, cancellationToken)
            .ConfigureAwait(true);
        Assert.True(
            adjustmentReplay.IsSuccess,
            adjustmentReplay.IsFailure ? adjustmentReplay.Error.Code : string.Empty);
        Assert.Equal(adjusted.Value, adjustmentReplay.Value);

        LedgerEvidence afterAdjustment = await ReadLedgerEvidenceAsync(
            scenario,
            attempt,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("155", afterAdjustment.ConsumedTokens);
        Assert.Equal(1, afterAdjustment.AttemptCount);
        Assert.Equal(1, afterAdjustment.AdjustmentCount);
        Assert.Equal(4, afterAdjustment.EventCount);
        Assert.Equal(4, afterAdjustment.EventOutboxCount);

        AttemptSettlementFact fact = await ReadFactAsync(
            attempt.AttemptId.AsEntityId(),
            cancellationToken).ConfigureAwait(true);
        Assert.Equal(new BigInteger(130), fact.Usage.Tokens.TotalTokens);
        Assert.Equal(SettlementUsageSource.Upstream, fact.Usage.Source);
        Assert.False(fact.Usage.IsEstimated);
        Assert.Equal(Model, fact.RequestedModel);
        Assert.Equal(Model, fact.UpstreamModel);
        Assert.Equal(200, fact.UpstreamHttpStatus);
        Assert.Null(fact.ErrorCode);
        Assert.False(fact.IsStreaming);
        AttemptUsageAdjustment factAdjustment = Assert.IsType<AttemptUsageAdjustment>(
            fact.Adjustment);
        Assert.Equal(new BigInteger(155), factAdjustment.CorrectedTokens.TotalTokens);
        Assert.Equal(new BigInteger(25), factAdjustment.DeltaTokens);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReservationFailureDistinguishesInsufficientReservedAndExhausted()
    {
        // Governing contracts: DEC-013 and AC-030.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        IGroupQuotaLedger ledger = Ledger();

        CrashScenario insufficientScenario = CrashScenario.Create() with { TotalTokens = 100 };
        await PrepareAdmissionFixtureAsync(insufficientScenario, cancellationToken)
            .ConfigureAwait(true);
        Result<ReserveQuotaResult> insufficient = await ledger
            .ReserveAsync(
                Command(insufficientScenario, insufficientScenario.PreDispatch),
                cancellationToken)
            .ConfigureAwait(true);
        AssertFailure(insufficient, "group_quota_insufficient", retryAfterSeconds: null);
        Assert.False(await UsageRequestExistsAsync(
            insufficientScenario.PreDispatch.RequestId,
            cancellationToken).ConfigureAwait(true));

        CrashScenario scenario = CrashScenario.Create() with { TotalTokens = 150 };
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        Result<ReserveQuotaResult> first = await ledger
            .ReserveAsync(Command(scenario, scenario.PreDispatch), cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(first);

        CrashAttempt blocked = NewAttempt(80, 50, 30);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(Command(scenario, blocked), cancellationToken)
            .ConfigureAwait(true);
        AssertFailure(reserved, "group_quota_reserved", retryAfterSeconds: 1);
        Assert.False(await UsageRequestExistsAsync(
            blocked.RequestId,
            cancellationToken).ConfigureAwait(true));

        Result<DispatchedReservationHandle> dispatched = await ledger
            .MarkDispatchedAsync(
                new MarkReservationDispatchedCommand(
                    first.Value.Reservation,
                    SettlementProvider.OpenAi,
                    Model,
                    new TokenEstimateSplit(80, 40)),
                cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(dispatched);
        DateTimeOffset completedAt = dispatched.Value.DispatchStartedAt.AddMilliseconds(1);
        Result<QuotaTransitionResult> settlement = await ledger
            .SettleAsync(
                new SettleReservationCommand(
                    dispatched.Value,
                    UsageAttemptOutcome.Succeeded,
                    200,
                    null,
                    null,
                    null,
                    completedAt,
                    UsageRequestOutcome.Succeeded,
                    new TokenUsage(100, 50, 0, 0, 0),
                    SettlementUsageSource.Upstream,
                    null),
                cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(settlement);
        Assert.Equal(new BigInteger(150), settlement.Value.Quota.ConsumedTokens);

        CrashAttempt exhaustedAttempt = NewAttempt(1, 1, 0);
        Result<ReserveQuotaResult> exhausted = await ledger
            .ReserveAsync(Command(scenario, exhaustedAttempt), cancellationToken)
            .ConfigureAwait(true);
        AssertFailure(exhausted, "group_quota_exhausted", retryAfterSeconds: null);
        Assert.False(await UsageRequestExistsAsync(
            exhaustedAttempt.RequestId,
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentReservationsCannotOversubscribeRemainingQuota()
    {
        // Governing contract: AC-011. Independent first attempts share the same
        // authoritative Group period and start at one barrier. The participant
        // count stays below PostgreSQL's reserved-connection boundary so this
        // test measures quota serialization rather than connection exhaustion.
        const int participantCount = 32;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create() with { TotalTokens = 100 };
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReserveQuotaCommand[] commands = Enumerable.Range(0, participantCount)
            .Select(_ => Command(scenario, NewAttempt(80, 50, 30)))
            .ToArray();
        Task<Result<ReserveQuotaResult>>[] tasks = commands
            .Select(async command =>
            {
                await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await ledger.ReserveAsync(command, cancellationToken).ConfigureAwait(false);
            })
            .ToArray();
        barrier.SetResult();
        Result<ReserveQuotaResult>[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Result<ReserveQuotaResult> winner = Assert.Single(results, static result => result.IsSuccess);
        Assert.Equal(new BigInteger(80), winner.Value.Quota.ReservedTokens);
        Result<ReserveQuotaResult>[] rejected = results.Where(static result => result.IsFailure).ToArray();
        Assert.Equal(participantCount - 1, rejected.Length);
        Assert.All(rejected, result => AssertFailure(result, "group_quota_reserved", 1));

        ConcurrencyEvidence evidence = await ReadConcurrencyEvidenceAsync(
            scenario.GroupId,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("80", evidence.ReservedTokens);
        Assert.Equal(1, evidence.RequestCount);
        Assert.Equal(1, evidence.ReservationCount);
        Assert.Equal(1, evidence.ReservedEventCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UsersShareTheSameGroupQuotaPeriod()
    {
        // Governing contract: AC-010. No User-scoped quota or remaining value
        // participates; both users settle into the same Group period.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        SharedAccessGrant second = await CreateSecondAccessGrantAsync(
            scenario,
            cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        CrashAttempt firstAttempt = NewAttempt(10, 6, 4);
        ReserveQuotaCommand firstCommand = Command(scenario, firstAttempt);
        Result<QuotaTransitionResult> first = await ReserveDispatchSettleAsync(
            ledger,
            firstCommand,
            inputTokens: 11,
            outputTokens: 4,
            cancellationToken).ConfigureAwait(true);
        AssertSuccess(first);

        CrashAttempt secondAttempt = NewAttempt(10, 5, 5);
        ReserveQuotaCommand secondCommand = Command(scenario, secondAttempt) with
        {
            UserId = second.UserId.AsEntityId(),
            ApiKeyId = second.ApiKeyId.AsEntityId(),
            SubscriptionId = second.SubscriptionId.AsEntityId(),
        };
        Result<QuotaTransitionResult> secondResult = await ReserveDispatchSettleAsync(
            ledger,
            secondCommand,
            inputTokens: 12,
            outputTokens: 8,
            cancellationToken).ConfigureAwait(true);
        AssertSuccess(secondResult);

        Assert.Equal(first.Value.PeriodId, secondResult.Value.PeriodId);
        Assert.Equal(new BigInteger(35), secondResult.Value.Quota.ConsumedTokens);
        Assert.Equal(BigInteger.Zero, secondResult.Value.Quota.ReservedTokens);
        Assert.Equal(
            "35",
            await ReadPeriodConsumedAsync(
                scenario.PeriodId,
                cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PreDispatchReleaseIsIdempotentAndDispatchFenceRejectsRelease()
    {
        // Governing contract: AC-039's M3-E2 primitive. This is explicit release,
        // not the existing expiry-path test.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        CrashScenario scenario = CrashScenario.Create();
        await PrepareAdmissionFixtureAsync(scenario, cancellationToken).ConfigureAwait(true);
        IGroupQuotaLedger ledger = Ledger();

        Result<ReserveQuotaResult> first = await ledger
            .ReserveAsync(Command(scenario, scenario.PreDispatch), cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(first);
        ReleaseReservationCommand releaseCommand = new(
            first.Value.Reservation,
            "confirmed before dispatch");
        Result<QuotaTransitionResult> released = await ledger
            .ReleaseAsync(releaseCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(released);
        Assert.Equal(ReservationStatus.Released, released.Value.Status);
        Assert.Equal(BigInteger.Zero, released.Value.Quota.ReservedTokens);
        Result<QuotaTransitionResult> releaseReplay = await ledger
            .ReleaseAsync(releaseCommand, cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(releaseReplay);
        Assert.Equal(released.Value, releaseReplay.Value);

        Result<ReserveQuotaResult> second = await ledger
            .ReserveAsync(Command(scenario, scenario.Dispatched), cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(second);
        Result<DispatchedReservationHandle> dispatched = await ledger
            .MarkDispatchedAsync(
                new MarkReservationDispatchedCommand(
                    second.Value.Reservation,
                    SettlementProvider.OpenAi,
                    Model,
                    new TokenEstimateSplit(140, 60)),
                cancellationToken)
            .ConfigureAwait(true);
        AssertSuccess(dispatched);
        Result<QuotaTransitionResult> forbiddenRelease = await ledger
            .ReleaseAsync(
                new ReleaseReservationCommand(
                    second.Value.Reservation,
                    "must not release after fence"),
                cancellationToken)
            .ConfigureAwait(true);
        AssertFailure(forbiddenRelease, "internal_error", retryAfterSeconds: null);

        LedgerEvidence evidence = await ReadLedgerEvidenceAsync(
            scenario,
            scenario.Dispatched,
            cancellationToken).ConfigureAwait(true);
        Assert.Equal("pending", evidence.ReservationStatus);
        Assert.Equal("200", evidence.ReservedTokens);
        Assert.Equal(2, evidence.EventCount);
        Assert.Equal(2, evidence.EventOutboxCount);
    }

    private IGroupQuotaLedger Ledger() =>
        _fixture.ApiServices.GetRequiredService<IGroupQuotaLedger>();

    private static ReserveQuotaCommand Command(
        CrashScenario scenario,
        CrashAttempt attempt) => new(
            attempt.RequestId.AsEntityId(),
            attempt.AttemptId.AsEntityId(),
            0,
            scenario.UserId.AsEntityId(),
            scenario.ApiKeyId.AsEntityId(),
            scenario.SubscriptionId.AsEntityId(),
            scenario.GroupId.AsEntityId(),
            scenario.AccountId.AsEntityId(),
            scenario.ChannelId.AsEntityId(),
            UsageRequestEndpoint.ChatCompletions,
            Model,
            null,
            attempt.EstimatedTokens,
            false,
            attempt.LeaseOwner);

    private static CrashAttempt NewAttempt(int total, int input, int output) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        total,
        input,
        output,
        $"m3e2-owner-{Guid.CreateVersion7():N}");

    private static async ValueTask<Result<QuotaTransitionResult>> ReserveDispatchSettleAsync(
        IGroupQuotaLedger ledger,
        ReserveQuotaCommand command,
        long inputTokens,
        long outputTokens,
        CancellationToken cancellationToken)
    {
        Result<ReserveQuotaResult> reserve = await ledger
            .ReserveAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (reserve.IsFailure)
        {
            return Result.Failure<QuotaTransitionResult>(
                reserve.Error.Code,
                reserve.Error.Description,
                reserve.Error.RetryAfterSeconds);
        }

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
        if (dispatch.IsFailure)
        {
            return Result.Failure<QuotaTransitionResult>(
                dispatch.Error.Code,
                dispatch.Error.Description,
                dispatch.Error.RetryAfterSeconds);
        }

        return await ledger.SettleAsync(
            new SettleReservationCommand(
                dispatch.Value,
                UsageAttemptOutcome.Succeeded,
                200,
                null,
                null,
                null,
                dispatch.Value.DispatchStartedAt.AddMilliseconds(1),
                UsageRequestOutcome.Succeeded,
                new TokenUsage(inputTokens, outputTokens, 0, 0, 0),
                SettlementUsageSource.Upstream,
                null),
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<AttemptSettlementFact> ReadFactAsync(
        EntityId attemptId,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await WorkerFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        IAttemptSettlementFactReader reader =
            _fixture.WorkerServices.GetRequiredService<IAttemptSettlementFactReader>();
        Result<AttemptSettlementFact> result = await reader
            .GetByAttemptIdAsync(attemptId, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.Value;
    }

    private async ValueTask<SharedAccessGrant> CreateSecondAccessGrantAsync(
        CrashScenario scenario,
        CancellationToken cancellationToken)
    {
        SharedAccessGrant grant = SharedAccessGrant.Create();
        IUnitOfWork unitOfWork = await ApiFactory()
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(unitOfWork.Context);
        using (NpgsqlCommand user = session.CreateCommand("""
                   INSERT INTO public.users (
                       id, email, normalized_email, display_name, password_hash, security_stamp
                   ) VALUES ($1, $2, $2, 'M3-E2 shared User', 'test-password-hash', $3);
                   """))
        {
            user.Parameters.AddWithValue(grant.UserId);
            user.Parameters.AddWithValue(grant.Email);
            user.Parameters.AddWithValue(Guid.CreateVersion7());
            await AssertSingleRowAsync(user, cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand role = session.CreateCommand("""
                   INSERT INTO public.user_roles (user_id, role_id, assigned_by)
                   VALUES ($1, $2, $1);
                   """))
        {
            role.Parameters.AddWithValue(grant.UserId);
            role.Parameters.AddWithValue(Guid.Parse(AdminRoleId));
            await AssertSingleRowAsync(role, cancellationToken).ConfigureAwait(false);
        }

        using (NpgsqlCommand subscription = session.CreateCommand("""
                   SELECT disposition
                   FROM public.poolai_subscription_assign(
                       $1, $2, $3,
                       clock_timestamp() - interval '1 minute',
                       clock_timestamp() + interval '1 day',
                       $2, 'M3-E2 shared Group quota');
                   """))
        {
            subscription.Parameters.AddWithValue(grant.SubscriptionId);
            subscription.Parameters.AddWithValue(grant.UserId);
            subscription.Parameters.AddWithValue(scenario.TemplateId);
            Assert.Equal(
                "created",
                Assert.IsType<string>(await subscription
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        using (NpgsqlCommand apiKey = session.CreateCommand("""
                   SELECT disposition
                   FROM public.poolai_api_key_create(
                       $1, $2, $3, 'M3-E2 shared key', $4, $5,
                       1::smallint, NULL, '[]'::jsonb);
                   """))
        {
            apiKey.Parameters.AddWithValue(grant.ApiKeyId);
            apiKey.Parameters.AddWithValue(grant.UserId);
            apiKey.Parameters.AddWithValue(scenario.GroupId);
            apiKey.Parameters.AddWithValue(grant.KeyPrefix);
            apiKey.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
            Assert.Equal(
                "created",
                Assert.IsType<string>(await apiKey
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false)));
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return grant;
    }

    private async ValueTask<LedgerEvidence> ReadLedgerEvidenceAsync(
        CrashScenario scenario,
        CrashAttempt attempt,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                reservation.status,
                reservation.actual_tokens::text,
                reservation.usage_source,
                period.consumed_tokens::text,
                period.reserved_tokens::text,
                request.status,
                request.attempt_count,
                (SELECT count(*)::integer FROM public.usage_attempts a
                 WHERE a.attempt_id = reservation.attempt_id),
                (SELECT count(*)::integer FROM public.usage_attempt_adjustments x
                 WHERE x.attempt_id = reservation.attempt_id),
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
            JOIN public.usage_requests request ON request.request_id = reservation.request_id
            WHERE reservation.group_id = $1 AND reservation.attempt_id = $2;
            """);
        command.Parameters.AddWithValue(scenario.GroupId);
        command.Parameters.AddWithValue(attempt.AttemptId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        LedgerEvidence evidence = new(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private async ValueTask<ConcurrencyEvidence> ReadConcurrencyEvidenceAsync(
        Guid groupId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT
                period.reserved_tokens::text,
                (SELECT count(*)::integer FROM public.usage_requests request
                 WHERE request.quota_group_id = quota.group_id),
                (SELECT count(*)::integer FROM public.group_token_reservations reservation
                 WHERE reservation.group_id = quota.group_id),
                (SELECT count(*)::integer FROM public.group_quota_events event
                 WHERE event.group_id = quota.group_id AND event.event_type = 'reserved')
            FROM public.group_token_quotas quota
            JOIN public.group_quota_periods period ON period.id = quota.current_period_id
            WHERE quota.group_id = $1;
            """);
        command.Parameters.AddWithValue(groupId);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        Assert.True(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        ConcurrencyEvidence evidence = new(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
        Assert.False(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return evidence;
    }

    private async ValueTask<bool> UsageRequestExistsAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM public.usage_requests WHERE request_id = $1
            );
            """);
        command.Parameters.AddWithValue(requestId);
        return Assert.IsType<bool>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private async ValueTask<string> ReadPeriodConsumedAsync(
        Guid periodId,
        CancellationToken cancellationToken)
    {
        using NpgsqlCommand command = _fixture.AdministratorDataSource.CreateCommand("""
            SELECT consumed_tokens::text
            FROM public.group_quota_periods
            WHERE id = $1;
            """);
        command.Parameters.AddWithValue(periodId);
        return Assert.IsType<string>(await command
            .ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false));
    }

    private static void AssertSuccess<T>(Result<T> result)
    {
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);
    }

    private static void AssertFailure<T>(
        Result<T> result,
        string code,
        long? retryAfterSeconds)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
        Assert.Equal(retryAfterSeconds, result.Error.RetryAfterSeconds);
    }

    private sealed record SharedAccessGrant(
        Guid UserId,
        Guid SubscriptionId,
        Guid ApiKeyId,
        string Email,
        string KeyPrefix)
    {
        internal static SharedAccessGrant Create()
        {
            string suffix = Guid.CreateVersion7().ToString("N");
            return new SharedAccessGrant(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                $"m3e2-shared-{suffix}@poolai.test",
                $"sk-m3e2{suffix[..8]}");
        }
    }

    private sealed record LedgerEvidence(
        string ReservationStatus,
        string? ActualTokens,
        string? UsageSource,
        string ConsumedTokens,
        string ReservedTokens,
        string RequestStatus,
        int RequestAttemptCount,
        int AttemptCount,
        int AdjustmentCount,
        int EventCount,
        int EventOutboxCount);

    private sealed record ConcurrencyEvidence(
        string ReservedTokens,
        int RequestCount,
        int ReservationCount,
        int ReservedEventCount);
}
#pragma warning restore MA0051
