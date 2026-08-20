using System.Numerics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.LoadTests;

#pragma warning disable MA0051 // The test keeps the full reserve/dispatch/settle replay chain visible as exit evidence.
[Collection(PostgresQuotaHotspotTestGroup.Name)]
public sealed class M3RepositoryExitHotspotTests(PostgresQuotaHotspotFixture fixture)
{
    private const int RequestCount = 128;
    private const int ExpectedAcceptedCount = 96;
    private const int MaxConcurrency = 32;
    private const long TokensPerRequest = 5;

    [Fact(Timeout = PostgresQuotaHotspotFixture.ClockRollbackProofHardTimeoutMilliseconds)]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Load")]
    public async Task M3RepositoryExitHotspotPreservesQuotaInvariants()
    {
        // ADR 0014 intentionally scopes this deterministic repository gate to
        // M3 ledger invariants. It is not the section 8.2 physical performance
        // certification, Gateway latency evidence, or an M6 release report.
        Assert.True(
            PostgresQuotaHotspotFixture.ClockRollbackTemporalFrontierOffset
                > TimeSpan.FromMilliseconds(
                    PostgresQuotaHotspotFixture.ClockRollbackProofHardTimeoutMilliseconds));
        Assert.True(
            PostgresQuotaHotspotFixture.ClockRollbackLeaseOffset
                > PostgresQuotaHotspotFixture.ClockRollbackTemporalFrontierOffset);
        Assert.True(
            PostgresQuotaHotspotFixture.ClockRollbackMaxOffset
                > PostgresQuotaHotspotFixture.ClockRollbackLeaseOffset);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            PostgresQuotaHotspotFixture.ClockRollbackLeaseOffset
                - PostgresQuotaHotspotFixture.ClockRollbackTemporalFrontierOffset);
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            PostgresQuotaHotspotFixture.ClockRollbackMaxOffset
                - PostgresQuotaHotspotFixture.ClockRollbackTemporalFrontierOffset);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        long totalTokens = (ExpectedAcceptedCount + 1) * TokensPerRequest;
        QuotaHotspotScenario scenario = await fixture
            .CreateScenarioAsync(totalTokens, cancellationToken)
            .ConfigureAwait(true);
        IGroupQuotaLedger ledger = fixture.ApiServices
            .GetRequiredService<IGroupQuotaLedger>();
        await ExecuteReleaseReplayAsync(
            ledger,
            scenario,
            HotspotAttempt.Create(-2),
            cancellationToken).ConfigureAwait(true);
        await ExecuteClockRollbackDispatchReplayAsync(
            fixture,
            ledger,
            scenario,
            HotspotAttempt.Create(-1),
            cancellationToken).ConfigureAwait(true);
        HotspotAttempt[] attempts = Enumerable.Range(0, RequestCount)
            .Select(HotspotAttempt.Create)
            .ToArray();
        int nextAttempt = -1;
        int ready = 0;
        int accepted = 0;
        int rejected = 0;
        TaskCompletionSource saturated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<LoadRunResult> pendingRun = BoundedLoadHarness.RunAsync(
            RequestCount,
            MaxConcurrency,
            ExecuteOneAsync,
            cancellationToken);
        await saturated.Task.WaitAsync(cancellationToken).ConfigureAwait(true);
        release.SetResult();
        LoadRunResult run = await pendingRun.ConfigureAwait(true);

        Assert.Equal(RequestCount, run.Scheduled);
        Assert.Equal(RequestCount, run.Completed);
        Assert.Equal(MaxConcurrency, run.PeakConcurrency);
        Assert.Equal(ExpectedAcceptedCount, accepted);
        Assert.Equal(RequestCount - ExpectedAcceptedCount, rejected);

        QuotaHotspotEvidence evidence = await fixture
            .ReadEvidenceAsync(scenario, cancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(
            totalTokens.ToString(CultureInfo.InvariantCulture),
            evidence.TotalTokens);
        Assert.Equal(
            totalTokens.ToString(CultureInfo.InvariantCulture),
            evidence.ConsumedTokens);
        Assert.Equal("0", evidence.ReservedTokens);
        Assert.Equal("0", evidence.RemainingTokens);
        Assert.Equal(ExpectedAcceptedCount + 2, evidence.RequestCount);
        Assert.Equal(ExpectedAcceptedCount + 2, evidence.ReservationCount);
        Assert.Equal(ExpectedAcceptedCount + 1, evidence.SettledReservationCount);
        Assert.Equal(1, evidence.ReleasedReservationCount);
        Assert.Equal(ExpectedAcceptedCount + 1, evidence.AttemptCount);
        Assert.Equal(ExpectedAcceptedCount + 2, evidence.ReservedEventCount);
        Assert.Equal(ExpectedAcceptedCount + 1, evidence.DispatchEventCount);
        Assert.Equal(ExpectedAcceptedCount + 1, evidence.SettledEventCount);
        Assert.Equal(1, evidence.ReleasedEventCount);
        Assert.Equal(2 + (ExpectedAcceptedCount + 2) + ((ExpectedAcceptedCount + 1) * 2),
            evidence.QuotaEventCount);
        Assert.Equal(evidence.QuotaEventCount, evidence.OutboxCount);
        Assert.Equal(ExpectedAcceptedCount + 1, evidence.AuditCount);
        Assert.Equal(0, evidence.DuplicateIdentityCount);
        Assert.Equal(0, evidence.InvariantViolationCount);
        Assert.Equal(0, evidence.NarrowNumericColumnCount);

        async ValueTask ExecuteOneAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref ready) == MaxConcurrency)
            {
                saturated.TrySetResult();
            }

            await release.Task.WaitAsync(token).ConfigureAwait(false);
            int index = Interlocked.Increment(ref nextAttempt);
            AttemptDisposition disposition = await ExecuteAttemptAsync(
                ledger,
                scenario,
                attempts[index],
                token).ConfigureAwait(false);
            if (disposition == AttemptDisposition.Accepted)
            {
                Interlocked.Increment(ref accepted);
            }
            else
            {
                Interlocked.Increment(ref rejected);
            }
        }
    }

    private static async ValueTask ExecuteClockRollbackDispatchReplayAsync(
        PostgresQuotaHotspotFixture fixture,
        IGroupQuotaLedger ledger,
        QuotaHotspotScenario scenario,
        HotspotAttempt attempt,
        CancellationToken cancellationToken)
    {
        ReserveQuotaCommand reserveCommand = ReserveCommand(scenario, attempt);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserved);
        Result<ReserveQuotaResult> reserveReplay = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserveReplay);
        Assert.Equal(reserved.Value.Reservation, reserveReplay.Value.Reservation);

        DateTimeOffset temporalFrontier = await fixture
            .AdvanceReservationTemporalFrontierAsync(
                attempt.AttemptId,
                cancellationToken)
            .ConfigureAwait(false);
        MarkReservationDispatchedCommand dispatchCommand = new(
            reserved.Value.Reservation,
            SettlementProvider.OpenAi,
            "gpt-m3-exit",
            new TokenEstimateSplit(3, 2));
        Result<DispatchedReservationHandle> dispatched = await ledger
            .MarkDispatchedAsync(dispatchCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(dispatched);
        Assert.True(dispatched.Value.DispatchStartedAt >= temporalFrontier);
        Result<DispatchedReservationHandle> dispatchReplay = await ledger
            .MarkDispatchedAsync(dispatchCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(dispatchReplay);
        Assert.Equal(dispatched.Value, dispatchReplay.Value);
        QuotaHotspotDispatchClockEvidence clockEvidence = await fixture
            .ReadDispatchClockEvidenceAsync(attempt.AttemptId, cancellationToken)
            .ConfigureAwait(false);
        Assert.Equal(temporalFrontier, dispatched.Value.DispatchStartedAt);
        Assert.Equal(temporalFrontier, clockEvidence.CreatedAt);
        Assert.Equal(temporalFrontier, clockEvidence.UpdatedAt);
        Assert.Equal(temporalFrontier, clockEvidence.DispatchStartedAt);
        Assert.Equal(temporalFrontier, clockEvidence.EventDispatchStartedAt);
        Assert.Equal(1, clockEvidence.DispatchEventCount);
        Assert.Equal(1, clockEvidence.DispatchOutboxCount);

        SettleReservationCommand settleCommand = new(
            dispatched.Value,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            null,
            null,
            dispatched.Value.DispatchStartedAt.AddMilliseconds(1),
            UsageRequestOutcome.Succeeded,
            new TokenUsage(
                new BigInteger(3),
                new BigInteger(2),
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.Upstream,
            null);
        Result<QuotaTransitionResult> settled = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(settled);
        Result<QuotaTransitionResult> settleReplay = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(settleReplay);
        Assert.Equal(settled.Value, settleReplay.Value);
    }

    private static async ValueTask ExecuteReleaseReplayAsync(
        IGroupQuotaLedger ledger,
        QuotaHotspotScenario scenario,
        HotspotAttempt attempt,
        CancellationToken cancellationToken)
    {
        ReserveQuotaCommand command = ReserveCommand(scenario, attempt);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(command, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserved);
        Result<ReserveQuotaResult> reserveReplay = await ledger
            .ReserveAsync(command, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserveReplay);
        Assert.Equal(reserved.Value.Reservation, reserveReplay.Value.Reservation);

        ReleaseReservationCommand releaseCommand = new(
            reserved.Value.Reservation,
            "M3 Exit deterministic pre-dispatch release replay");
        Result<QuotaTransitionResult> released = await ledger
            .ReleaseAsync(releaseCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(released);
        Result<QuotaTransitionResult> releaseReplay = await ledger
            .ReleaseAsync(releaseCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(releaseReplay);
        Assert.Equal(released.Value, releaseReplay.Value);
    }

    private static async ValueTask<AttemptDisposition> ExecuteAttemptAsync(
        IGroupQuotaLedger ledger,
        QuotaHotspotScenario scenario,
        HotspotAttempt attempt,
        CancellationToken cancellationToken)
    {
        ReserveQuotaCommand reserveCommand = ReserveCommand(scenario, attempt);
        Result<ReserveQuotaResult> reserved = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(false);
        if (reserved.IsFailure)
        {
            Assert.True(
                reserved.Error.Code is "group_quota_reserved" or "group_quota_exhausted");
            Assert.Equal(
                string.Equals(
                    reserved.Error.Code,
                    "group_quota_reserved",
                    StringComparison.Ordinal)
                    ? 1
                    : null,
                reserved.Error.RetryAfterSeconds);
            return AttemptDisposition.Rejected;
        }

        AssertSuccess(reserved);
        Result<ReserveQuotaResult> reserveReplay = await ledger
            .ReserveAsync(reserveCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(reserveReplay);
        Assert.Equal(reserved.Value.Status, reserveReplay.Value.Status);
        Assert.Equal(reserved.Value.Reservation, reserveReplay.Value.Reservation);

        MarkReservationDispatchedCommand dispatchCommand = new(
            reserved.Value.Reservation,
            SettlementProvider.OpenAi,
            "gpt-m3-exit",
            new TokenEstimateSplit(3, 2));
        Result<DispatchedReservationHandle> dispatched = await ledger
            .MarkDispatchedAsync(dispatchCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(dispatched);
        Result<DispatchedReservationHandle> dispatchReplay = await ledger
            .MarkDispatchedAsync(dispatchCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(dispatchReplay);
        Assert.Equal(dispatched.Value, dispatchReplay.Value);

        DateTimeOffset completedAt = dispatched.Value.DispatchStartedAt.AddMilliseconds(1);
        SettleReservationCommand settleCommand = new(
            dispatched.Value,
            UsageAttemptOutcome.Succeeded,
            200,
            null,
            null,
            null,
            completedAt,
            UsageRequestOutcome.Succeeded,
            new TokenUsage(
                new BigInteger(3),
                new BigInteger(2),
                BigInteger.Zero,
                BigInteger.Zero,
                BigInteger.Zero),
            SettlementUsageSource.Upstream,
            null);
        Result<QuotaTransitionResult> settled = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(settled);
        Result<QuotaTransitionResult> settleReplay = await ledger
            .SettleAsync(settleCommand, cancellationToken)
            .ConfigureAwait(false);
        AssertSuccess(settleReplay);
        Assert.Equal(settled.Value.ReservationId, settleReplay.Value.ReservationId);
        Assert.Equal(settled.Value.AttemptId, settleReplay.Value.AttemptId);
        Assert.Equal(settled.Value.PeriodId, settleReplay.Value.PeriodId);
        Assert.Equal(settled.Value.Status, settleReplay.Value.Status);
        Assert.True(settled.Value.Quota.ConsumedTokens >= BigInteger.Zero);
        Assert.True(settled.Value.Quota.ReservedTokens >= BigInteger.Zero);
        Assert.True(
            settled.Value.Quota.ConsumedTokens + settled.Value.Quota.ReservedTokens
            <= settled.Value.Quota.TotalTokens);
        return AttemptDisposition.Accepted;
    }

    private static ReserveQuotaCommand ReserveCommand(
        QuotaHotspotScenario scenario,
        HotspotAttempt attempt) => new(
            new EntityId(attempt.RequestId),
            new EntityId(attempt.AttemptId),
            0,
            new EntityId(scenario.UserId),
            new EntityId(scenario.ApiKeyId),
            new EntityId(scenario.SubscriptionId),
            new EntityId(scenario.GroupId),
            new EntityId(scenario.AccountId),
            new EntityId(scenario.ChannelId),
            UsageRequestEndpoint.Responses,
            "gpt-m3-exit",
            null,
            TokensPerRequest,
            false,
            attempt.LeaseOwner);

    private static void AssertSuccess<T>(Result<T> result) =>
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : string.Empty);

    private sealed record HotspotAttempt(Guid RequestId, Guid AttemptId, string LeaseOwner)
    {
        public static HotspotAttempt Create(int index)
        {
            uint identity = checked((uint)(index + 2));
            return new HotspotAttempt(
                Guid.Parse($"019b90c1-0000-7000-8000-{identity:x12}"),
                Guid.Parse($"019b90c2-0000-7000-8000-{identity:x12}"),
                $"m3-exit-owner-{identity:D3}");
        }
    }

    private enum AttemptDisposition
    {
        Accepted,
        Rejected,
    }
}
#pragma warning restore MA0051
