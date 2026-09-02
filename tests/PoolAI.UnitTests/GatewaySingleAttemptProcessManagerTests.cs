using System.Net;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - ADR 0015, Process Manager and route/credential handoff.
// - docs/contracts/error-catalog.md, four attempt phases.
// - docs/开发执行规格-v1.0.md, DEC-022/023/039 and M4-E1.
public sealed class GatewaySingleAttemptProcessManagerTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        2,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task RunsFrozenOrderAndSettlesLosslessUsageBeforeLeaseRelease()
    {
        Harness harness = new();
        harness.Adapter.SendResult = SuccessfulResult();

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(GatewaySingleAttemptDisposition.Succeeded,
            result.Value.Disposition);
        Assert.Equal(GatewayAttemptPhase.BusinessOutputStarted,
            result.Value.Phase);
        AssertOrdered(harness.Events,
            "api-key", "user", "subscription", "group", "route",
            "reserve", "credential-acquire", "prepare", "account-renew",
            "dispatch", "send",
            "credential-deliver", "settle", "account-release");
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(new BigInteger(31), settlement.Usage.InputTokens);
        Assert.Equal(new BigInteger(17), settlement.Usage.OutputTokens);
        Assert.Equal(SettlementUsageSource.Upstream, settlement.UsageSource);
        Assert.Equal(UsageAttemptOutcome.Succeeded, settlement.AttemptOutcome);
        Assert.Empty(harness.Ledger.Releases);
        Assert.True(harness.Transport.CredentialObservedAfterFence);
    }

    [Fact]
    public async Task InitialRequestsUseUuidV7AndFreshAttemptIdentities()
    {
        EntityId firstRequestId =
            Id("01920000-0000-7000-8000-000000000009");
        EntityId secondRequestId =
            Id("01920000-0000-7000-8000-00000000000a");
        Harness firstHarness = new();
        Harness secondHarness = new();

        Result<GatewaySingleAttemptOutcome> first = await firstHarness
            .ExecuteAsync(requestId: firstRequestId);
        Result<GatewaySingleAttemptOutcome> second = await secondHarness
            .ExecuteAsync(requestId: secondRequestId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(7, first.Value.RequestId.Value.Version);
        Assert.Equal(7, second.Value.RequestId.Value.Version);
        Assert.Equal(7, first.Value.AttemptId.Value.Version);
        Assert.Equal(7, second.Value.AttemptId.Value.Version);
        Assert.Equal(firstRequestId, first.Value.RequestId);
        Assert.Equal(secondRequestId, second.Value.RequestId);
        Assert.Equal(0, first.Value.AttemptIndex);
        Assert.Equal(0, second.Value.AttemptIndex);
        Assert.NotEqual(first.Value.RequestId, second.Value.RequestId);
        Assert.NotEqual(first.Value.AttemptId, second.Value.AttemptId);
        Assert.Equal(
            first.Value.AttemptId,
            Assert.Single(firstHarness.Ledger.Reservations).AttemptId);
        Assert.Equal(
            second.Value.AttemptId,
            Assert.Single(secondHarness.Ledger.Reservations).AttemptId);
    }

    [Fact]
    public async Task CredentialFailureReleasesReservationAndStopsBeforePrepare()
    {
        Harness harness = new();
        harness.Credentials.FailureCode = "dependency_unavailable";

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Single(harness.Ledger.Releases);
        Assert.Empty(harness.Ledger.Settlements);
        Assert.DoesNotContain("prepare", harness.Events);
        Assert.DoesNotContain("dispatch", harness.Events);
        Assert.DoesNotContain("send", harness.Events);
        AssertOrdered(harness.Events,
            "reserve", "credential-acquire", "release", "account-release");
    }

    [Fact]
    public async Task TotalAttemptDeadlineCancelsSlowRoutingBeforeReservationOrDispatch()
    {
        Harness harness = new();
        harness.Router.BlockUntilCancelled = true;

        Task<Result<GatewaySingleAttemptOutcome>> execution = harness
            .ExecuteAsync(Now.AddSeconds(1))
            .AsTask();
        await harness.Router.Entered.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        Result<GatewaySingleAttemptOutcome> result = await execution.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("upstream_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Empty(harness.Ledger.Reservations);
        Assert.DoesNotContain("credential-acquire", harness.Events);
        Assert.DoesNotContain("dispatch", harness.Events);
        Assert.DoesNotContain("send", harness.Events);
    }

    [Fact]
    public async Task TotalAttemptDeadlineReleasesReservationWhenCredentialReadStalls()
    {
        Harness harness = new();
        harness.Credentials.BlockUntilCancelled = true;

        Task<Result<GatewaySingleAttemptOutcome>> execution = harness
            .ExecuteAsync(Now.AddSeconds(1))
            .AsTask();
        await harness.Credentials.Entered.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        Result<GatewaySingleAttemptOutcome> result = await execution.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("upstream_unavailable", result.Error.Code);
        Assert.Single(harness.Ledger.Releases);
        Assert.Empty(harness.Ledger.Settlements);
        Assert.DoesNotContain("prepare", harness.Events);
        Assert.DoesNotContain("dispatch", harness.Events);
        Assert.DoesNotContain("send", harness.Events);
    }

    [Fact]
    public async Task DispatchFailureCannotSendBytesAndReleasesPreFence()
    {
        Harness harness = new();
        harness.Ledger.FailDispatch = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Single(harness.Ledger.Releases);
        Assert.Empty(harness.Ledger.Settlements);
        Assert.DoesNotContain("send", harness.Events);
        Assert.DoesNotContain("credential-deliver", harness.Events);
        Assert.Equal(GatewayAttemptPhase.Prepared, harness.Adapter.Context!.Phase);
        Assert.False(harness.Adapter.Context.RequestBytesWritten);
        AssertOrdered(harness.Events,
            "prepare", "dispatch", "release", "account-release");
    }

    [Fact]
    public async Task LostAccountLeaseImmediatelyBeforeDispatchReleasesWithoutSending()
    {
        Harness harness = new();
        harness.AccountLease.RenewResult = AccountLeaseRenewResult.Lost;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("account_capacity_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(harness.Ledger.Releases);
        Assert.Empty(harness.Ledger.Settlements);
        Assert.DoesNotContain("dispatch", harness.Events);
        Assert.DoesNotContain("send", harness.Events);
        AssertOrdered(
            harness.Events,
            "prepare",
            "account-renew",
            "release",
            "account-release");
    }

    [Fact]
    public async Task UnavailableAccountRenewalFailsClosedBeforeDispatch()
    {
        Harness harness = new();
        harness.AccountLease.RenewResult = AccountLeaseRenewResult.Unavailable;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("coordination_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Single(harness.Ledger.Releases);
        Assert.Empty(harness.Ledger.Settlements);
        Assert.DoesNotContain("dispatch", harness.Events);
        Assert.DoesNotContain("send", harness.Events);
    }

    [Fact]
    public async Task DispatchedAmbiguitySettlesEstimateAndNeverReleases()
    {
        Harness harness = new();
        harness.Adapter.SendResult = Result.Success(new NormalizedUpstreamResult(
            StatusCode: 502,
            Payload: JsonSerializer.SerializeToElement(new { }),
            Usage: null,
            ErrorCode: "coordination_unavailable"));

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        Assert.True(result.Value.Lifetime.SettledConservatively);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_dispatch_ambiguous", settlement.ErrorCode);
        Assert.Equal("upstream_dispatch_ambiguous", result.Value.ErrorCode);
        Assert.Equal(settlement.Reservation.Estimate.InputTokens,
            checked((long)settlement.Usage.InputTokens));
        Assert.Equal(settlement.Reservation.Estimate.OutputTokens,
            checked((long)settlement.Usage.OutputTokens));
        Assert.Empty(harness.Ledger.Releases);
    }

    [Fact]
    public async Task PostDispatchAttemptDeadlineIsNotMisclassifiedAsClientDisconnect()
    {
        Harness harness = new();
        harness.Transport.WaitForAttemptDeadline = true;

        Task<Result<GatewaySingleAttemptOutcome>> execution = harness
            .ExecuteAsync(Now.AddSeconds(1))
            .AsTask();
        await harness.Transport.SendEntered.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(1));

        Result<GatewaySingleAttemptOutcome> result = await execution.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        Assert.Equal(
            ReservationLifetimeStopReason.AttemptDeadlineReached,
            result.Value.Lifetime.StopReason);
        Assert.Equal("upstream_dispatch_ambiguous", result.Value.ErrorCode);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(UsageAttemptOutcome.Failed, settlement.AttemptOutcome);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Empty(harness.Ledger.Releases);
    }

    [Fact]
    public async Task AttemptDeadlineDrainsAndSettlesNonCooperativeTransport()
    {
        Harness harness = new();
        harness.Transport.IgnoreCancellationUntilReleased = true;

        Task<Result<GatewaySingleAttemptOutcome>> execution = harness
            .ExecuteAsync(Now.AddSeconds(1))
            .AsTask();
        await harness.Transport.SendEntered.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Transport.AbortObserved.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);

        Result<GatewaySingleAttemptOutcome> result = await execution.WaitAsync(
            TestContext.Current.CancellationToken);
        harness.Transport.ReleaseIgnoredSend.TrySetResult();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        Assert.Equal(
            ReservationLifetimeStopReason.AttemptDeadlineReached,
            result.Value.Lifetime.StopReason);
        Assert.True(result.Value.Lifetime.DrainTimedOut);
        Assert.True(result.Value.Lifetime.SettledConservatively);
        Assert.Equal("upstream_dispatch_ambiguous", result.Value.ErrorCode);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(UsageAttemptOutcome.Failed, settlement.AttemptOutcome);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Contains("prepared-dispose", harness.Events);
        Assert.Contains("account-release", harness.Events);
        Assert.Empty(harness.Ledger.Releases);
    }

    [Fact]
    public async Task PostDispatchReservationDeadlineUsesInternalErrorSettlement()
    {
        Harness harness = new();
        harness.Ledger.ReservationDeadline = Now;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        Assert.Equal("internal_error", result.Value.ErrorCode);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(SettlementUsageSource.ConfirmedNoExecution,
            settlement.UsageSource);
        Assert.Equal("internal_error", settlement.ErrorCode);
        Assert.DoesNotContain("send", harness.Events);
        Assert.Empty(harness.Ledger.Releases);
        AssertOrdered(harness.Events,
            "dispatch", "settle", "account-release");
    }

    [Theory]
    [InlineData(TerminationEvidenceCase.PreFenceRelease)]
    [InlineData(TerminationEvidenceCase.PostFenceConservativeSettlement)]
    [InlineData(TerminationEvidenceCase.AccountLeaseLost)]
    [InlineData(TerminationEvidenceCase.TwoCoordinationFailures)]
    [InlineData(TerminationEvidenceCase.AttemptDeadlineDrain)]
    public async Task KillPointAndLeaseCancellationMatrixFinalizesQuotaExactlyOnce(
        TerminationEvidenceCase scenario)
    {
        Harness harness = new();

        Result<GatewaySingleAttemptOutcome> result =
            await ExecuteTerminationCaseAsync(harness, scenario);

        Assert.Equal(
            1,
            harness.Ledger.Releases.Count + harness.Ledger.Settlements.Count);
        int dispatchIndex = IndexOf(harness.Events, "dispatch");
        int sendIndex = IndexOf(harness.Events, "send");
        if (scenario == TerminationEvidenceCase.PreFenceRelease)
        {
            Assert.True(result.IsFailure);
            Assert.Single(harness.Ledger.Releases);
            Assert.Empty(harness.Ledger.Settlements);
            Assert.Equal(-1, sendIndex);
            return;
        }

        Assert.True(result.IsSuccess);
        Assert.Empty(harness.Ledger.Releases);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.True(dispatchIndex >= 0 && sendIndex > dispatchIndex);
        AssertTerminationReason(result.Value, scenario);
    }

    [Fact]
    public async Task ConfirmedNoExecutionSettlesZeroAfterFence()
    {
        Harness harness = new();
        harness.Adapter.SendResult = Result.Failure<NormalizedUpstreamResult>(
            "upstream_unavailable",
            "The scripted transport did not write a request.");
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedNotWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(SettlementUsageSource.ConfirmedNoExecution,
            settlement.UsageSource);
        Assert.Equal(BigInteger.Zero, settlement.Usage.TotalTokens);
        Assert.Empty(harness.Ledger.Releases);
    }

    [Fact]
    public async Task AdapterCannotClaimConfirmedNoExecutionWithoutCapability()
    {
        Harness harness = new(AdapterCapability(
            canProveNoRequestBytesWritten: false));
        harness.Adapter.SendResult = Result.Failure<NormalizedUpstreamResult>(
            "upstream_unavailable",
            "The scripted transport did not write a request.");
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedNotWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_dispatch_ambiguous", settlement.ErrorCode);
    }

    [Theory]
    [InlineData(401, AdapterRejectedStatusEvidence.Unauthorized)]
    [InlineData(403, AdapterRejectedStatusEvidence.Forbidden)]
    [InlineData(429, AdapterRejectedStatusEvidence.TooManyRequests)]
    public async Task RegisteredRejectedStatusCapabilityCanSettleWrittenRejectionAtZero(
        int statusCode,
        AdapterRejectedStatusEvidence rejectedStatusEvidence)
    {
        Harness harness = new(AdapterCapability(
            rejectedStatusEvidence: rejectedStatusEvidence));
        harness.Adapter.SendResult = Result.Success(new NormalizedUpstreamResult(
            StatusCode: statusCode,
            Payload: JsonSerializer.SerializeToElement(new { }),
            Usage: null,
            ErrorCode: "upstream_authentication_error"));
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConfirmedNoExecution,
            settlement.UsageSource);
        Assert.Equal(BigInteger.Zero, settlement.Usage.TotalTokens);
    }

    [Fact]
    public async Task WrittenRejectedStatusWithoutMatchingRegisteredBitIsAmbiguous()
    {
        Harness harness = new(AdapterCapability(
            rejectedStatusEvidence: AdapterRejectedStatusEvidence.Forbidden));
        harness.Adapter.SendResult = Result.Success(new NormalizedUpstreamResult(
            StatusCode: 401,
            Payload: JsonSerializer.SerializeToElement(new { }),
            Usage: null,
            ErrorCode: "upstream_authentication_error"));
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_dispatch_ambiguous", settlement.ErrorCode);
    }

    [Fact]
    public async Task WrittenRejectedStatusWithUsageSettlesExactBeforeProtocolFailure()
    {
        Harness harness = new(AdapterCapability(
            rejectedStatusEvidence: AdapterRejectedStatusEvidence.Unauthorized));
        NormalizedUpstreamResult withUsage = SuccessfulResult().Value with
        {
            StatusCode = 401,
            ErrorCode = "upstream_authentication_error",
        };
        harness.Adapter.SendResult = Result.Success(withUsage);
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.Upstream,
            settlement.UsageSource);
        Assert.Equal("upstream_protocol_error", settlement.ErrorCode);
        Assert.Equal(new BigInteger(31), settlement.Usage.InputTokens);
        Assert.Equal(new BigInteger(17), settlement.Usage.OutputTokens);
    }

    [Fact]
    public async Task InvalidMetadataCannotDiscardReliableExactUsage()
    {
        Harness harness = new();
        harness.Adapter.SendResult = Result.Success(
            SuccessfulResult().Value with
            {
                UpstreamRequestId = "   ",
            });

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(SettlementUsageSource.Upstream, settlement.UsageSource);
        Assert.Equal(new BigInteger(31), settlement.Usage.InputTokens);
        Assert.Equal(new BigInteger(17), settlement.Usage.OutputTokens);
        Assert.Equal("upstream_protocol_error", settlement.ErrorCode);
    }

    [Fact]
    public async Task ParsedFailureOnWrittenRejectionCannotSettleAtZero()
    {
        Harness harness = new(AdapterCapability(
            rejectedStatusEvidence: AdapterRejectedStatusEvidence.Unauthorized));
        harness.Adapter.SendResult = Result.Failure<NormalizedUpstreamResult>(
            "upstream_protocol_error",
            "The scripted parser rejected the response.");
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_protocol_error", settlement.ErrorCode);
    }

    [Fact]
    public async Task AdapterCapabilityMustExactlyMatchRegisteredPolicy()
    {
        Harness harness = new(
            AdapterCapability(
                rejectedStatusEvidence:
                    AdapterRejectedStatusEvidence.Unauthorized),
            registeredCapability: AdapterCapability());

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Empty(harness.Ledger.Reservations);
        Assert.DoesNotContain("credential-acquire", harness.Events);
        Assert.DoesNotContain("prepare", harness.Events);
        Assert.DoesNotContain("dispatch", harness.Events);
    }

    [Fact]
    public async Task PrepareTimeCapabilityMutationCannotEscalateRegisteredPolicy()
    {
        Harness harness = new();
        harness.Adapter.CapabilityAfterPrepare = AdapterCapability(
            rejectedStatusEvidence: AdapterRejectedStatusEvidence.Unauthorized);
        harness.Adapter.SendResult = Result.Success(new NormalizedUpstreamResult(
            StatusCode: 401,
            Payload: JsonSerializer.SerializeToElement(new { }),
            Usage: null,
            ErrorCode: "upstream_authentication_error"));
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_dispatch_ambiguous", settlement.ErrorCode);
        Assert.Equal(
            AdapterRejectedStatusEvidence.None,
            harness.Transport.LastCapability!
                .ConfirmedNoExecutionStatuses);
    }

    [Fact]
    public async Task ContradictorySuccessfulNoExecutionEvidenceIsProtocolFailure()
    {
        Harness harness = new();
        harness.Adapter.SendResult = Result.Success(new NormalizedUpstreamResult(
            StatusCode: 200,
            Payload: JsonSerializer.SerializeToElement(new { id = "response" }),
            Usage: null,
            ErrorCode: null));
        harness.Transport.WriteEvidence =
            GatewayRequestWriteEvidence.ConfirmedNotWritten;
        harness.Transport.ConfirmedNoExecution = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(
            GatewaySingleAttemptDisposition.Failed,
            result.Value.Disposition);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(
            SettlementUsageSource.ConservativeEstimate,
            settlement.UsageSource);
        Assert.Equal("upstream_protocol_error", settlement.ErrorCode);
    }

    [Fact]
    public async Task FutureFirstTokenEvidenceNeverAdvancesCompletionTime()
    {
        Harness harness = new();
        NormalizedUpstreamResult successful = SuccessfulResult().Value with
        {
            FirstTokenAt = Now.AddHours(1),
        };
        harness.Adapter.SendResult = Result.Success(successful);

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsSuccess);
        SettleReservationCommand settlement = Assert.Single(
            harness.Ledger.Settlements);
        Assert.Equal(Now, settlement.CompletedAt);
        Assert.Null(settlement.FirstTokenAt);
    }

    [Fact]
    public async Task SettlementFailureAfterDispatchIsNeverExposedAsRetryable()
    {
        Harness harness = new();
        harness.Ledger.FailSettlement = true;

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("upstream_dispatch_ambiguous", result.Error.Code);
        Assert.Null(result.Error.RetryAfterSeconds);
        Assert.Single(harness.Ledger.Settlements);
        Assert.Empty(harness.Ledger.Releases);
    }

    [Fact]
    public async Task NumericOverflowDuringSettlementKeepsTheRequiredP0Code()
    {
        Harness harness = new();
        harness.Ledger.SettlementFailureCode = "token_numeric_overflow";

        Result<GatewaySingleAttemptOutcome> result = await harness.ExecuteAsync();

        Assert.True(result.IsFailure);
        Assert.Equal("token_numeric_overflow", result.Error.Code);
        Assert.Null(result.Error.RetryAfterSeconds);
        Assert.DoesNotContain("78", result.Error.Description, StringComparison.Ordinal);
        Assert.Single(harness.Ledger.Settlements);
        Assert.Empty(harness.Ledger.Releases);
    }

    private static Result<NormalizedUpstreamResult> SuccessfulResult()
    {
        JsonElement raw = JsonSerializer.SerializeToElement(new
        {
            input_tokens = 31,
            output_tokens = 17,
        });
        NormalizedUpstreamUsage usage = new(
            new BigInteger(31),
            new BigInteger(17),
            new BigInteger(4),
            new BigInteger(3),
            new BigInteger(5),
            raw);
        return Result.Success(new NormalizedUpstreamResult(
            StatusCode: 200,
            Payload: JsonSerializer.SerializeToElement(new { id = "response" }),
            Usage: usage,
            ErrorCode: null,
            UpstreamRequestId: "upstream-request",
            FirstTokenAt: Now));
    }

    private static async Task<Result<GatewaySingleAttemptOutcome>>
        ExecuteTerminationCaseAsync(
            Harness harness,
            TerminationEvidenceCase scenario)
    {
        if (scenario == TerminationEvidenceCase.PreFenceRelease)
        {
            harness.Ledger.FailDispatch = true;
            return await harness.ExecuteAsync().ConfigureAwait(false);
        }

        if (scenario == TerminationEvidenceCase.PostFenceConservativeSettlement)
        {
            harness.Adapter.SendResult = Result.Success(
                new NormalizedUpstreamResult(
                    StatusCode: 502,
                    Payload: JsonSerializer.SerializeToElement(new { }),
                    Usage: null,
                    ErrorCode: "coordination_unavailable",
                    UpstreamRequestId: null,
                    FirstTokenAt: null));
            return await harness.ExecuteAsync().ConfigureAwait(false);
        }

        return await ExecuteCancellationCaseAsync(harness, scenario)
            .ConfigureAwait(false);
    }

    private static async Task<Result<GatewaySingleAttemptOutcome>>
        ExecuteCancellationCaseAsync(
            Harness harness,
            TerminationEvidenceCase scenario)
    {
        harness.Transport.IgnoreCancellationUntilReleased = true;
        DateTimeOffset? deadline = scenario
            == TerminationEvidenceCase.AttemptDeadlineDrain
                ? Now.AddSeconds(1)
                : null;
        Task<Result<GatewaySingleAttemptOutcome>> execution = harness
            .ExecuteAsync(deadline)
            .AsTask();
        await harness.Transport.SendEntered.Task.WaitAsync(
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        await TriggerCancellationAsync(harness, scenario).ConfigureAwait(false);
        await harness.Transport.AbortObserved.Task.WaitAsync(
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        harness.Time.Advance(
            ReservationLifetimeCoordinator.MaximumDrainDuration);
        Result<GatewaySingleAttemptOutcome> result = await execution.WaitAsync(
                TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        harness.Transport.ReleaseIgnoredSend.TrySetResult();
        return result;
    }

    private static async Task TriggerCancellationAsync(
        Harness harness,
        TerminationEvidenceCase scenario)
    {
        if (scenario == TerminationEvidenceCase.AccountLeaseLost)
        {
            harness.AccountLease.RenewResult = AccountLeaseRenewResult.Lost;
            harness.Time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
            return;
        }

        if (scenario == TerminationEvidenceCase.TwoCoordinationFailures)
        {
            harness.AccountLease.RenewResult = AccountLeaseRenewResult.Unavailable;
            harness.Time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
            await PumpAsync().ConfigureAwait(false);
            Assert.Equal(2, harness.AccountLease.RenewalCount);
            Assert.False(harness.Transport.AbortObserved.Task.IsCompleted);
            harness.Time.Advance(AccountLeaseLifetimeOperation.RenewInterval);
            return;
        }

        Assert.Equal(TerminationEvidenceCase.AttemptDeadlineDrain, scenario);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
    }

    private static void AssertTerminationReason(
        GatewaySingleAttemptOutcome outcome,
        TerminationEvidenceCase scenario)
    {
        if (scenario == TerminationEvidenceCase.AccountLeaseLost)
        {
            Assert.Equal(
                AccountLeaseLifetimeStopReason.LeaseLost,
                outcome.AccountLeaseStopReason);
        }
        else if (scenario == TerminationEvidenceCase.TwoCoordinationFailures)
        {
            Assert.Equal(
                AccountLeaseLifetimeStopReason.CoordinationUnavailable,
                outcome.AccountLeaseStopReason);
        }
        else if (scenario == TerminationEvidenceCase.AttemptDeadlineDrain)
        {
            Assert.Equal(
                ReservationLifetimeStopReason.AttemptDeadlineReached,
                outcome.Lifetime.StopReason);
            Assert.True(outcome.Lifetime.DrainTimedOut);
        }
    }

    private static async Task PumpAsync()
    {
        for (int iteration = 0; iteration < 10; iteration++)
        {
            await Task.Yield();
        }
    }

    private static void AssertOrdered(
        IReadOnlyList<string> events,
        params string[] expected)
    {
        int previous = -1;
        foreach (string value in expected)
        {
            int current = IndexOf(events, value);
            Assert.True(current > previous,
                $"Expected '{value}' after index {previous}: {string.Join(',', events)}");
            previous = current;
        }
    }

    private static int IndexOf(IReadOnlyList<string> events, string value)
    {
        for (int index = 0; index < events.Count; index++)
        {
            if (string.Equals(events[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private static AdapterCapability AdapterCapability(
        bool canProveNoRequestBytesWritten = true,
        AdapterRejectedStatusEvidence rejectedStatusEvidence =
            AdapterRejectedStatusEvidence.None) => new(
        InboundProtocol.Responses,
        UpstreamType.OpenAi,
        AdapterOperation.NonStream,
        canProveNoRequestBytesWritten,
        SupportsVerifiedIdempotentReplay: false,
        rejectedStatusEvidence);

    public enum TerminationEvidenceCase
    {
        PreFenceRelease,
        PostFenceConservativeSettlement,
        AccountLeaseLost,
        TwoCoordinationFailures,
        AttemptDeadlineDrain,
    }

    private sealed class Harness
    {
        internal static readonly EntityId ApiKeyId =
            Id("01920000-0000-7000-8000-000000000001");
        internal static readonly EntityId UserId =
            Id("01920000-0000-7000-8000-000000000002");
        internal static readonly EntityId GroupId =
            Id("01920000-0000-7000-8000-000000000003");
        internal static readonly EntityId SubscriptionId =
            Id("01920000-0000-7000-8000-000000000004");
        internal static readonly EntityId ChannelId =
            Id("01920000-0000-7000-8000-000000000005");
        internal static readonly EntityId AccountId =
            Id("01920000-0000-7000-8000-000000000006");
        internal static readonly EntityId PeriodId =
            Id("01920000-0000-7000-8000-000000000007");
        internal static readonly EntityId ReservationId =
            Id("01920000-0000-7000-8000-000000000008");

        private readonly GatewaySingleAttemptProcessManager _manager;
        private readonly GatewayCanonicalAdmissionService _canonical;

        internal Harness(
            AdapterCapability? adapterCapability = null,
            AdapterCapability? registeredCapability = null)
        {
            Events = [];
            Time = new FakeTimeProvider(Now);
            FakeApiKeyAuthenticator apiKeys = new(Events);
            FakeUserReader users = new(Events);
            FakeSubscriptionReader subscriptions = new(Events);
            FakeGroupReader groups = new(Events);
            _canonical = new GatewayCanonicalAdmissionService(
                apiKeys,
                users,
                subscriptions,
                groups,
                new GatewayClientIpResolver(new GatewayIngressOptions()));
            Ledger = new FakeQuotaLedger(Events, Time);
            Credentials = new FakeCredentialSource(Events);
            Adapter = new FakeAdapter(Events);
            if (adapterCapability is not null)
            {
                Adapter.Capability = adapterCapability;
            }

            Transport = new FakeTransport(Events, Adapter, Time);
            AccountLease = new FakeAccountLease(Events, Time);
            Router = new FakeAccountRouter(Events, AccountLease);
            ReservationLifetimeCoordinator reservationLifetime = new(
                Ledger,
                Time,
                ReservationLifetimeCoordinator.MaximumDrainDuration);
            _manager = new GatewaySingleAttemptProcessManager(
                new ConservativeTokenEstimator(new GatewayEstimationOptions()),
                Router,
                Ledger,
                new GatewayCredentialHandoff(Credentials),
                Transport,
                [Adapter],
                new AdapterCapabilityRegistry(
                    [registeredCapability ?? Adapter.Capability]),
                Time,
                reservationLifetime);
        }

        internal List<string> Events { get; }

        internal FakeTimeProvider Time { get; }

        internal FakeQuotaLedger Ledger { get; }

        internal FakeCredentialSource Credentials { get; }

        internal FakeAdapter Adapter { get; }

        internal FakeTransport Transport { get; }

        internal FakeAccountLease AccountLease { get; }

        internal FakeAccountRouter Router { get; }

        internal async ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
            DateTimeOffset? deadline = null,
            EntityId? requestId = null)
        {
            Result<GatewayCanonicalAccess> canonical = await _canonical
                .AuthorizeAsync(
                    "poolai-test-key",
                    IPAddress.Parse("203.0.113.10"),
                    forwardedForFieldValues: null,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
            Assert.True(canonical.IsSuccess);
            NormalizedGatewayRequest normalized = new(
                requestId ?? Id("01920000-0000-7000-8000-000000000009"),
                "gpt-m4-e1",
                Stream: false,
                JsonSerializer.SerializeToElement(new
                {
                    model = "gpt-m4-e1",
                    input = "hello",
                    max_output_tokens = 64,
                }));
            GatewaySingleAttemptRequest request = new(
                canonical.Value,
                InboundProtocol.Responses,
                normalized,
                attemptIndex: 0,
                clientRequestId: null,
                leaseOwner: "api:test",
                deadline: deadline ?? Now.AddMinutes(2),
                remainingRetryBudget: 0);
            return await _manager.ExecuteAsync(
                    request,
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class FakeApiKeyAuthenticator(List<string> events) :
        IApiKeyAuthenticator
    {
        public ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
            string presentedKey,
            CancellationToken cancellationToken)
        {
            events.Add("api-key");
            return ValueTask.FromResult(Result.Success(new ApiKeyAccessSnapshot(
                Harness.ApiKeyId,
                Harness.UserId,
                Harness.GroupId,
                IsEffective: true,
                AllowedCidrs: [],
                Version: 1,
                ObservedAt: Now)));
        }
    }

    private sealed class FakeUserReader(List<string> events) : IUserStatusReader
    {
        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            events.Add("user");
            return ValueTask.FromResult(Result.Success(new UserStatusSnapshot(
                Harness.UserId,
                UserLifecycle.Active,
                SystemRole.User,
                TokenVersion: 1,
                Version: 1,
                ObservedAt: Now)));
        }
    }

    private sealed class FakeSubscriptionReader(List<string> events) :
        ISubscriptionAccessReader
    {
        public ValueTask<Result<SubscriptionAccessSnapshot>> GetEffectiveAccessAsync(
            EntityId userId,
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            events.Add("subscription");
            return ValueTask.FromResult(Result.Success(
                new SubscriptionAccessSnapshot(
                    Harness.SubscriptionId,
                    Harness.UserId,
                    Harness.GroupId,
                    "test-plan",
                    Now.AddDays(-1),
                    Now.AddDays(1),
                    SubscriptionEffectiveStatus.Active,
                    Version: 1,
                    ObservedAt: Now)));
        }
    }

    private sealed class FakeGroupReader(List<string> events) : IGroupStatusReader
    {
        public ValueTask<Result<GroupSnapshot>> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            events.Add("group");
            return ValueTask.FromResult(Result.Success(new GroupSnapshot(
                Harness.GroupId,
                GroupLifecycle.Active,
                Version: 7,
                HasCurrentQuotaPeriod: true,
                ObservedAt: Now,
                RequestsPerMinute: 6000)));
        }
    }

    private sealed class FakeAccountRouter(
        List<string> events,
        IAccountLease lease) : IAccountRouter
    {
        internal bool BlockUntilCancelled { get; set; }

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Result<IAccountLease>> RouteAsync(
            RouteAccountCommand command,
            CancellationToken cancellationToken)
        {
            events.Add("route");
            Entered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Result.Success(lease);
        }
    }

    private sealed class FakeAccountLease : IAccountLease
    {
        private readonly List<string> _events;

        internal FakeAccountLease(List<string> events, TimeProvider time)
        {
            _events = events;
            Route = new AccountRoute(
                Harness.GroupId,
                Harness.ChannelId,
                Harness.AccountId,
                AccountRouteProvider.OpenAi,
                "gpt-m4-e1",
                "gpt-upstream",
                new Uri("https://api.example.test/v1/", UriKind.Absolute),
                new AccountRouteCapabilities(
                    Responses: true,
                    ChatCompletions: true,
                    FunctionTools: true,
                    Streaming: true),
                time.GetUtcNow().AddMinutes(1),
                SupplyConfigurationVersion: 3,
                ChannelVersion: 4,
                AccountVersion: 5,
                CredentialRevision: 6);
        }

        public AccountRoute Route { get; }

        internal AccountLeaseRenewResult? RenewResult { get; set; }

        internal int RenewalCount { get; private set; }

        public ValueTask<AccountLeaseRenewResult> RenewAsync(
            CancellationToken cancellationToken)
        {
            _events.Add("account-renew");
            RenewalCount++;
            return ValueTask.FromResult(
                RenewResult ?? AccountLeaseRenewResult.Renewed(Route));
        }

        public ValueTask<Result<bool>> ReleaseAsync(
            CancellationToken cancellationToken)
        {
            _events.Add("account-release");
            return ValueTask.FromResult(Result.Success(true));
        }

        public ValueTask DisposeAsync()
        {
            _events.Add("account-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCredentialSource(List<string> events) :
        IRouteCredentialLeaseSource
    {
        internal string? FailureCode { get; set; }

        internal bool BlockUntilCancelled { get; set; }

        internal TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
            RouteCredentialLeaseRequest request,
            CancellationToken cancellationToken)
        {
            events.Add("credential-acquire");
            Entered.TrySetResult();
            if (BlockUntilCancelled)
            {
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return FailureCode is null
                ? Result.Success<IRouteCredentialLease>(
                    new FakeCredentialLease(events))
                : Result.Failure<IRouteCredentialLease>(
                    FailureCode,
                    "The scripted credential acquisition failed.",
                    retryAfterSeconds: 1);
        }
    }

    private sealed class FakeCredentialLease(List<string> events) :
        IRouteCredentialLease
    {
        private byte[]? _secret = "test-upstream-secret"u8.ToArray();

        public void TransferOnce(RouteCredentialReader reader)
        {
            byte[] secret = _secret
                ?? throw new InvalidOperationException("Credential already used.");
            _secret = null;
            events.Add("credential-deliver");
            try
            {
                reader(secret);
            }
            finally
            {
                Array.Clear(secret);
            }
        }

        public void Dispose()
        {
            if (_secret is not null)
            {
                Array.Clear(_secret);
                _secret = null;
            }
        }
    }

    private sealed class FakeAdapter(List<string> events) :
        IUpstreamAdapter,
        IPreparedUpstreamAttempt
    {
        internal FakeAdapter()
            : this([])
        {
        }

        public AdapterCapability Capability { get; internal set; } = new(
            InboundProtocol.Responses,
            UpstreamType.OpenAi,
            AdapterOperation.NonStream,
            CanProveNoRequestBytesWritten: true,
            SupportsVerifiedIdempotentReplay: false);

        internal AdapterAttemptContext? Context { get; private set; }

        internal AdapterCapability? CapabilityAfterPrepare { get; set; }

        internal Result<NormalizedUpstreamResult> SendResult { get; set; } =
            SuccessfulResult();

        public ValueTask<Result<IPreparedUpstreamAttempt>> PrepareAsync(
            AdapterAttemptContext attempt,
            NormalizedGatewayRequest request,
            CancellationToken cancellationToken)
        {
            events.Add("prepare");
            Context = attempt;
            if (CapabilityAfterPrepare is not null)
            {
                Capability = CapabilityAfterPrepare;
            }

            return ValueTask.FromResult(
                Result.Success<IPreparedUpstreamAttempt>(this));
        }

        public ValueTask<Result<PreparedUpstreamRequest>> CreateRequestAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdapterAttemptContext context = Context
                ?? throw new InvalidOperationException("Adapter was not prepared.");
            return ValueTask.FromResult(Result.Success(
                new PreparedUpstreamRequest(
                    HttpMethod.Post,
                    new Uri(context.Route.UpstreamBaseUri, "responses"),
                    "{}"u8)));
        }

        public ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
            AdapterUpstreamResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(SendResult);
        }

        public ValueTask DisposeAsync()
        {
            events.Add("prepared-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransport(
        List<string> events,
        FakeAdapter adapter,
        TimeProvider timeProvider) : IGatewayUpstreamTransport
    {
        internal GatewayRequestWriteEvidence WriteEvidence { get; set; } =
            GatewayRequestWriteEvidence.ConfirmedWritten;

        internal bool ConfirmedNoExecution { get; set; }

        internal bool CredentialObservedAfterFence { get; private set; }

        internal AdapterCapability? LastCapability { get; private set; }

        internal bool WaitForAttemptDeadline { get; set; }

        internal bool IgnoreCancellationUntilReleased { get; set; }

        internal TaskCompletionSource SendEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AbortObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseIgnoredSend { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<GatewayUpstreamTransportResult> SendAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            AdapterAttemptContext attemptContext,
            AdapterCapability capability,
            IUpstreamCredentialHandle credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("send");
            SendEntered.TrySetResult();
            LastCapability = capability;
            if (attemptContext.Phase
                < GatewayAttemptPhase.DispatchedNoDownstreamHeaders)
            {
                throw new InvalidOperationException(
                    "The test transport observed a pre-fence send.");
            }

            CredentialObservedAfterFence = credential is not null;
            events.Add("credential-deliver");
            if (IgnoreCancellationUntilReleased)
            {
                return await WaitForReleaseIgnoringCancellationAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (WaitForAttemptDeadline)
            {
                return await WaitUntilDeadlineAsync(attemptContext.Deadline)
                    .ConfigureAwait(false);
            }

            if (adapter.SendResult.IsSuccess
                && adapter.SendResult.Value.StatusCode is >= 200 and <= 299)
            {
                attemptContext.OutputEvidenceSink
                    .MarkDownstreamHeadersCommitted();
                attemptContext.OutputEvidenceSink.MarkBusinessOutputStarted();
            }

            return new GatewayUpstreamTransportResult(
                adapter.SendResult,
                WriteEvidence,
                ConfirmedNoExecution);
        }

        private async ValueTask<GatewayUpstreamTransportResult>
            WaitForReleaseIgnoringCancellationAsync(
                CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration =
                cancellationToken.Register(
                    () => AbortObserved.TrySetResult());
            await ReleaseIgnoredSend.Task.ConfigureAwait(false);
            return FailedAfterWrite(
                "The scripted transport was released after cancellation.");
        }

        private async ValueTask<GatewayUpstreamTransportResult>
            WaitUntilDeadlineAsync(DateTimeOffset deadlineAt)
        {
            TimeSpan remaining = deadlineAt - timeProvider.GetUtcNow();
            using CancellationTokenSource deadline = new(
                remaining,
                timeProvider);
            try
            {
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        deadline.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested)
            {
            }

            return FailedAfterWrite(
                "The scripted transport reached the attempt deadline.");
        }

        private static GatewayUpstreamTransportResult FailedAfterWrite(
            string description) => new(
            Result.Failure<NormalizedUpstreamResult>(
                "upstream_unavailable",
                description),
            GatewayRequestWriteEvidence.ConfirmedWritten,
            ConfirmedNoExecution: false);
    }

    private sealed class FakeQuotaLedger(
        List<string> events,
        TimeProvider time) : IGroupQuotaLedger
    {
        internal bool FailDispatch { get; set; }

        internal bool FailSettlement { get; set; }

        internal string? SettlementFailureCode { get; set; }

        internal DateTimeOffset? ReservationDeadline { get; set; }

        internal List<ReserveQuotaCommand> Reservations { get; } = [];

        internal List<SettleReservationCommand> Settlements { get; } = [];

        internal List<ReleaseReservationCommand> Releases { get; } = [];

        public ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
            ReserveQuotaCommand command,
            CancellationToken cancellationToken)
        {
            events.Add("reserve");
            Reservations.Add(command);
            ReservationHandle reservation = Reservation(command);
            return ValueTask.FromResult(Result.Success(new ReserveQuotaResult(
                ReservationStatus.Pending,
                reservation,
                Position(reservation))));
        }

        public ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
            MarkReservationDispatchedCommand command,
            CancellationToken cancellationToken)
        {
            events.Add("dispatch");
            return ValueTask.FromResult(FailDispatch
                ? Result.Failure<DispatchedReservationHandle>(
                    "dependency_unavailable",
                    "The scripted dispatch fence failed.",
                    retryAfterSeconds: 1)
                : Result.Success(new DispatchedReservationHandle(
                    ReservationStatus.Pending,
                    command.Reservation,
                    command.Provider,
                    command.Model,
                    command.Estimate,
                    time.GetUtcNow())));
        }

        public ValueTask<Result<ReservationHandle>> RenewAsync(
            RenewReservationCommand command,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(command.Reservation));

        public ValueTask<Result<QuotaTransitionResult>> SettleAsync(
            SettleReservationCommand command,
            CancellationToken cancellationToken)
        {
            events.Add("settle");
            Settlements.Add(command);
            return ValueTask.FromResult(FailSettlement
                || SettlementFailureCode is not null
                ? Result.Failure<QuotaTransitionResult>(
                    SettlementFailureCode ?? "dependency_unavailable",
                    "The scripted settlement failed.",
                    retryAfterSeconds: SettlementFailureCode is null ? 1 : null)
                : Result.Success(Transition(
                    command.Reservation.Reservation,
                    ReservationStatus.Settled)));
        }

        public ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
            ReleaseReservationCommand command,
            CancellationToken cancellationToken)
        {
            events.Add("release");
            Releases.Add(command);
            return ValueTask.FromResult(Result.Success(Transition(
                command.Reservation,
                ReservationStatus.Released)));
        }

        private ReservationHandle Reservation(ReserveQuotaCommand command)
        {
            DateTimeOffset maximum = ReservationDeadline
                ?? time.GetUtcNow().AddMinutes(10);
            DateTimeOffset lease = ReservationDeadline
                ?? time.GetUtcNow().AddMinutes(5);
            return new ReservationHandle(
                Harness.ReservationId,
                command.RequestId,
                command.AttemptId,
                command.AttemptIndex,
                command.GroupId,
                Harness.PeriodId,
                command.AccountId,
                command.ChannelId,
                command.EstimatedTokens,
                command.IsStreaming,
                command.LeaseOwner,
                lease,
                maximum);
        }

        private static QuotaLedgerPosition Position(
            ReservationHandle reservation) => new(
            reservation.GroupId,
            reservation.PeriodId,
            new BigInteger(1_000_000),
            BigInteger.Zero,
            new BigInteger(reservation.EstimatedTokens),
            new BigInteger(1_000_000 - reservation.EstimatedTokens));

        private static QuotaTransitionResult Transition(
            ReservationHandle reservation,
            ReservationStatus status) => new(
            reservation.ReservationId,
            reservation.AttemptId,
            reservation.PeriodId,
            status,
            Position(reservation));
    }
}
