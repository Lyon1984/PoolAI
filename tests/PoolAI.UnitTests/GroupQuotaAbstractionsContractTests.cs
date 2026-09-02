using System.Numerics;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - docs/README.md, D-001/D-002/D-004: Group-only quota ownership,
//   lossless Token facts, and distinct request/attempt identities.
// - docs/开发执行规格-v1.0.md, M3-E1..M3-E4 delivery boundaries.
public sealed class GroupQuotaAbstractionsContractTests
{
    private const long EstimatedTokens = 1_500;
    private static readonly EntityId GroupId = Id(1);
    private static readonly EntityId PeriodId = Id(2);
    private static readonly EntityId RequestId = Id(3);
    private static readonly EntityId AttemptId = Id(4);
    private static readonly EntityId AccountId = Id(5);
    private static readonly EntityId ReservationId = Id(6);
    private static readonly EntityId UserId = Id(7);
    private static readonly EntityId ApiKeyId = Id(8);
    private static readonly EntityId SubscriptionId = Id(9);
    private static readonly EntityId ChannelId = Id(10);
    private static readonly EntityId QuotaEventId = Id(11);
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void AttemptSettlementFactPreservesEveryPublishedUsageFact()
    {
        (TokenUsage tokens, AttemptUsage usage, AttemptUsageAdjustment adjustment) =
            CreateAdjustedUsage();
        DateTimeOffset dispatchStartedAt = ObservedAt.AddSeconds(-3);
        DateTimeOffset firstTokenAt = ObservedAt.AddSeconds(-2);
        AttemptSettlementFact fact = new(
            AttemptId,
            RequestId,
            AttemptIndex: 2,
            ReservationId,
            GroupId,
            PeriodId,
            AccountId,
            ChannelId,
            SettlementProvider.OpenAiCompatible,
            "gpt-requested",
            "gpt-contract",
            UsageAttemptOutcome.Succeeded,
            UpstreamHttpStatus: 200,
            ErrorCode: null,
            IsStreaming: true,
            Usage: usage,
            Adjustment: adjustment,
            DispatchStartedAt: dispatchStartedAt,
            FirstTokenAt: firstTokenAt,
            CompletedAt: ObservedAt);

        Assert.Equal(AttemptId, fact.AttemptId);
        Assert.Equal(RequestId, fact.RequestId);
        Assert.Equal(2, fact.AttemptIndex);
        Assert.Equal(ReservationId, fact.ReservationId);
        Assert.Equal(GroupId, fact.GroupId);
        Assert.Equal(PeriodId, fact.PeriodId);
        Assert.Equal(AccountId, fact.AccountId);
        Assert.Equal(ChannelId, fact.ChannelId);
        Assert.Equal(SettlementProvider.OpenAiCompatible, fact.Provider);
        Assert.Equal("gpt-requested", fact.RequestedModel);
        Assert.Equal("gpt-contract", fact.UpstreamModel);
        Assert.Equal(UsageAttemptOutcome.Succeeded, fact.Outcome);
        Assert.Equal(200, fact.UpstreamHttpStatus);
        Assert.Null(fact.ErrorCode);
        Assert.True(fact.IsStreaming);
        Assert.Same(usage, fact.Usage);
        Assert.Same(adjustment, fact.Adjustment);
        AttemptUsageAdjustment actualAdjustment = Assert.IsType<AttemptUsageAdjustment>(
            fact.Adjustment);
        Assert.Equal(dispatchStartedAt, fact.DispatchStartedAt);
        Assert.Equal(firstTokenAt, fact.FirstTokenAt);
        Assert.Equal(ObservedAt, fact.CompletedAt);
        Assert.Equal(tokens.InputTokens + tokens.OutputTokens, tokens.TotalTokens);
        Assert.Equal(SettlementUsageSource.Upstream, fact.Usage.Source);
        Assert.False(fact.Usage.IsEstimated);
        Assert.Equal(QuotaEventId, actualAdjustment.QuotaEventId);
        Assert.Equal(tokens.TotalTokens, actualAdjustment.PreviousTotalTokens);
        Assert.Same(adjustment.CorrectedTokens, actualAdjustment.CorrectedTokens);
        Assert.Equal(SettlementUsageSource.Upstream, actualAdjustment.Source);
        Assert.Equal(new BigInteger(50), actualAdjustment.DeltaTokens);
        Assert.Equal(ObservedAt.AddMinutes(1), actualAdjustment.AdjustedAt);
    }

    [Fact]
    public void AttemptSettlementFactAllowsNoAdjustmentOrFirstToken()
    {
        AttemptSettlementFact fact = new(
            AttemptId,
            RequestId,
            AttemptIndex: 0,
            ReservationId,
            GroupId,
            PeriodId,
            AccountId,
            ChannelId,
            SettlementProvider.OpenAi,
            "gpt-requested",
            "gpt-contract",
            UsageAttemptOutcome.Failed,
            UpstreamHttpStatus: 401,
            ErrorCode: "invalid_api_key",
            IsStreaming: false,
            Usage: new AttemptUsage(
                ZeroUsage(),
                SettlementUsageSource.ConfirmedNoExecution,
                IsEstimated: false),
            Adjustment: null,
            DispatchStartedAt: ObservedAt,
            FirstTokenAt: null,
            CompletedAt: ObservedAt.AddSeconds(1));

        Assert.Null(fact.Adjustment);
        Assert.Null(fact.FirstTokenAt);
        Assert.Equal("invalid_api_key", fact.ErrorCode);
        Assert.Equal(SettlementUsageSource.ConfirmedNoExecution, fact.Usage.Source);
    }

    [Fact]
    public void GroupActivationResultPreservesResourceAndVersionFence()
    {
        GroupResourceSnapshot resource = new(
            GroupId,
            "Research",
            "Primary research Group",
            GroupLifecycle.Active,
            17,
            ObservedAt.AddDays(-1),
            ObservedAt,
            RequestsPerMinute: 6000);
        GroupActivationResult result = new(
            GroupId,
            GroupLifecycle.Active,
            17,
            resource);

        Assert.Equal(GroupId, result.GroupId);
        Assert.Equal(GroupLifecycle.Active, result.Lifecycle);
        Assert.Equal(17, result.Version);
        Assert.Same(resource, result.Resource);

        GroupActivationResult withoutResource = new(
            GroupId,
            GroupLifecycle.Disabled,
            18);
        Assert.Null(withoutResource.Resource);
    }

    [Fact]
    public void GroupSnapshotPreservesQuotaReadinessObservation()
    {
        GroupSnapshot snapshot = new(
            GroupId,
            GroupLifecycle.Disabled,
            21,
            HasCurrentQuotaPeriod: true,
            ObservedAt,
            RequestsPerMinute: 6000);

        Assert.Equal(GroupId, snapshot.GroupId);
        Assert.Equal(GroupLifecycle.Disabled, snapshot.Lifecycle);
        Assert.Equal(21, snapshot.Version);
        Assert.True(snapshot.HasCurrentQuotaPeriod);
        Assert.Equal(ObservedAt, snapshot.ObservedAt);
        Assert.Equal(6000, snapshot.RequestsPerMinute);
    }

    [Fact]
    public void QuotaSnapshotPreservesSafeIntegerAuthorityState()
    {
        BigInteger total = new(9_007_199_254_740_991L);
        BigInteger consumed = total - 200;
        BigInteger reserved = new(75);
        QuotaSnapshot snapshot = new(
            GroupId,
            PeriodId,
            total,
            consumed,
            reserved,
            34);

        Assert.Equal(GroupId, snapshot.GroupId);
        Assert.Equal(PeriodId, snapshot.PeriodId);
        Assert.Equal(total, snapshot.Total);
        Assert.Equal(consumed, snapshot.Consumed);
        Assert.Equal(reserved, snapshot.Reserved);
        Assert.Equal(34, snapshot.Version);
    }

    [Fact]
    public void ReserveQuotaCommandPreservesAdmissionAndRoutingBoundaries()
    {
        ReserveQuotaCommand command = CreateReserveCommand();

        Assert.Equal(RequestId, command.RequestId);
        Assert.Equal(AttemptId, command.AttemptId);
        Assert.Equal(2, command.AttemptIndex);
        Assert.Equal(UserId, command.UserId);
        Assert.Equal(ApiKeyId, command.ApiKeyId);
        Assert.Equal(SubscriptionId, command.SubscriptionId);
        Assert.Equal(GroupId, command.GroupId);
        Assert.Equal(AccountId, command.AccountId);
        Assert.Equal(ChannelId, command.ChannelId);
        Assert.Equal(UsageRequestEndpoint.Responses, command.Endpoint);
        Assert.Equal("gpt-contract", command.RequestedModel);
        Assert.Equal("client-request-17", command.ClientRequestId);
        Assert.Equal(EstimatedTokens, command.EstimatedTokens);
        Assert.True(command.IsStreaming);
        Assert.Equal("gateway-17", command.LeaseOwner);
    }

    [Fact]
    public void ReservationHandlePreservesIdentityRouteAndLeaseFences()
    {
        ReservationHandle handle = CreateReservation();

        Assert.Equal(ReservationId, handle.ReservationId);
        Assert.Equal(RequestId, handle.RequestId);
        Assert.Equal(AttemptId, handle.AttemptId);
        Assert.Equal(2, handle.AttemptIndex);
        Assert.Equal(GroupId, handle.GroupId);
        Assert.Equal(PeriodId, handle.PeriodId);
        Assert.Equal(AccountId, handle.AccountId);
        Assert.Equal(ChannelId, handle.ChannelId);
        Assert.Equal(EstimatedTokens, handle.EstimatedTokens);
        Assert.True(handle.IsStreaming);
        Assert.Equal("gateway-17", handle.LeaseOwner);
        Assert.Equal(ObservedAt.AddMinutes(2), handle.LeaseExpiresAt);
        Assert.Equal(ObservedAt.AddHours(2), handle.MaxExpiresAt);
    }

    [Fact]
    public void RenewCommandPreservesReservationAndStablePositiveSequence()
    {
        ReservationHandle reservation = CreateReservation();
        RenewReservationCommand command = new(reservation, RenewalSequence: 17);

        Assert.Same(reservation, command.Reservation);
        Assert.Equal(17, command.RenewalSequence);
    }

    [Fact]
    public void ReserveResultPreservesStatusHandleAndLosslessLedgerPosition()
    {
        ReservationHandle reservation = CreateReservation();
        QuotaLedgerPosition quota = CreateQuotaPosition();
        ReserveQuotaResult result = new(
            ReservationStatus.Pending,
            reservation,
            quota);

        Assert.Equal(ReservationStatus.Pending, result.Status);
        Assert.Same(reservation, result.Reservation);
        Assert.Same(quota, result.Quota);
        Assert.Equal(GroupId, quota.GroupId);
        Assert.Equal(PeriodId, quota.PeriodId);
        Assert.Equal(new BigInteger(9_007_199_254_740_991L), quota.TotalTokens);
        Assert.Equal(new BigInteger(2_000), quota.ConsumedTokens);
        Assert.Equal(new BigInteger(1_500), quota.ReservedTokens);
        Assert.Equal(new BigInteger(9_007_199_254_737_491L), quota.RemainingTokens);
    }

    [Fact]
    public void DispatchContractsPreserveProviderModelEstimateAndFenceTime()
    {
        ReservationHandle reservation = CreateReservation();
        TokenEstimateSplit estimate = new(900, 600);
        MarkReservationDispatchedCommand command = new(
            reservation,
            SettlementProvider.OpenAi,
            "gpt-contract",
            estimate);
        DateTimeOffset dispatchStartedAt = ObservedAt.AddSeconds(1);
        DispatchedReservationHandle dispatched = new(
            ReservationStatus.Pending,
            reservation,
            SettlementProvider.OpenAi,
            "gpt-contract",
            estimate,
            dispatchStartedAt);

        Assert.Same(reservation, command.Reservation);
        Assert.Equal(SettlementProvider.OpenAi, command.Provider);
        Assert.Equal("gpt-contract", command.Model);
        Assert.Same(estimate, command.Estimate);
        Assert.Equal(900, estimate.InputTokens);
        Assert.Equal(600, estimate.OutputTokens);
        Assert.Equal(ReservationStatus.Pending, dispatched.Status);
        Assert.Same(reservation, dispatched.Reservation);
        Assert.Equal(SettlementProvider.OpenAi, dispatched.Provider);
        Assert.Equal("gpt-contract", dispatched.Model);
        Assert.Same(estimate, dispatched.Estimate);
        Assert.Equal(dispatchStartedAt, dispatched.DispatchStartedAt);
    }

    [Fact]
    public void SettlementCommandPreservesExactUsageAndOptionalUpstreamEvidence()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"input_tokens":1200,"output_tokens":700}""");
        JsonElement rawUpstreamUsage = document.RootElement.Clone();
        DispatchedReservationHandle dispatched = CreateDispatchedReservation();
        TokenUsage usage = new(
            new BigInteger(1_200),
            new BigInteger(700),
            new BigInteger(200),
            new BigInteger(100),
            new BigInteger(300));
        DateTimeOffset firstTokenAt = ObservedAt.AddSeconds(2);
        DateTimeOffset completedAt = ObservedAt.AddSeconds(3);
        SettleReservationCommand command = new(
            dispatched,
            UsageAttemptOutcome.Succeeded,
            UpstreamHttpStatus: 200,
            ErrorCode: null,
            "upstream-request-17",
            firstTokenAt,
            completedAt,
            UsageRequestOutcome.Succeeded,
            usage,
            SettlementUsageSource.Upstream,
            rawUpstreamUsage);

        Assert.Same(dispatched, command.Reservation);
        Assert.Equal(UsageAttemptOutcome.Succeeded, command.AttemptOutcome);
        Assert.Equal(200, command.UpstreamHttpStatus);
        Assert.Null(command.ErrorCode);
        Assert.Equal("upstream-request-17", command.UpstreamRequestId);
        Assert.Equal(firstTokenAt, command.FirstTokenAt);
        Assert.Equal(completedAt, command.CompletedAt);
        Assert.Equal(UsageRequestOutcome.Succeeded, command.RequestOutcome);
        Assert.Same(usage, command.Usage);
        Assert.Equal(SettlementUsageSource.Upstream, command.UsageSource);
        Assert.True(command.RawUpstreamUsage.HasValue);
        Assert.Equal(
            JsonValueKind.Object,
            command.RawUpstreamUsage.GetValueOrDefault().ValueKind);
        Assert.Equal(new BigInteger(1_900), usage.TotalTokens);
        Assert.Equal(new BigInteger(1_200), usage.InputTokens);
        Assert.Equal(new BigInteger(700), usage.OutputTokens);
        Assert.Equal(new BigInteger(200), usage.CacheReadTokens);
        Assert.Equal(new BigInteger(100), usage.CacheCreationTokens);
        Assert.Equal(new BigInteger(300), usage.ThinkingTokens);
    }

    [Fact]
    public void ReleaseAndTransitionContractsPreserveTerminalState()
    {
        ReservationHandle reservation = CreateReservation();
        ReleaseReservationCommand command = new(
            reservation,
            "confirmed before dispatch");
        QuotaLedgerPosition quota = CreateQuotaPosition();
        QuotaTransitionResult result = new(
            ReservationId,
            AttemptId,
            PeriodId,
            ReservationStatus.Released,
            quota);

        Assert.Same(reservation, command.Reservation);
        Assert.Equal("confirmed before dispatch", command.Reason);
        Assert.Equal(ReservationId, result.ReservationId);
        Assert.Equal(AttemptId, result.AttemptId);
        Assert.Equal(PeriodId, result.PeriodId);
        Assert.Equal(ReservationStatus.Released, result.Status);
        Assert.Same(quota, result.Quota);
    }

    [Fact]
    public void LedgerPortsPublishAllFourTransitionsAndTransactionalFactRead()
    {
        Type ledger = typeof(IGroupQuotaLedger);
        Assert.NotNull(ledger.GetMethod(nameof(IGroupQuotaLedger.ReserveAsync)));
        Assert.NotNull(ledger.GetMethod(nameof(IGroupQuotaLedger.MarkDispatchedAsync)));
        Assert.NotNull(ledger.GetMethod(nameof(IGroupQuotaLedger.RenewAsync)));
        Assert.NotNull(ledger.GetMethod(nameof(IGroupQuotaLedger.SettleAsync)));
        Assert.NotNull(ledger.GetMethod(nameof(IGroupQuotaLedger.ReleaseAsync)));

        Type[] factReaderParameters = typeof(IAttemptSettlementFactReader)
            .GetMethod(nameof(IAttemptSettlementFactReader.GetByAttemptIdAsync))!
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.Equal(
            [typeof(EntityId), typeof(IUnitOfWorkContext), typeof(CancellationToken)],
            factReaderParameters);
    }

    private static ReserveQuotaCommand CreateReserveCommand() => new(
        RequestId,
        AttemptId,
        AttemptIndex: 2,
        UserId,
        ApiKeyId,
        SubscriptionId,
        GroupId,
        AccountId,
        ChannelId,
        UsageRequestEndpoint.Responses,
        "gpt-contract",
        "client-request-17",
        EstimatedTokens,
        IsStreaming: true,
        "gateway-17");

    private static ReservationHandle CreateReservation() => new(
        ReservationId,
        RequestId,
        AttemptId,
        AttemptIndex: 2,
        GroupId,
        PeriodId,
        AccountId,
        ChannelId,
        EstimatedTokens,
        IsStreaming: true,
        "gateway-17",
        ObservedAt.AddMinutes(2),
        ObservedAt.AddHours(2));

    private static DispatchedReservationHandle CreateDispatchedReservation() => new(
        ReservationStatus.Pending,
        CreateReservation(),
        SettlementProvider.OpenAi,
        "gpt-contract",
        new TokenEstimateSplit(900, 600),
        ObservedAt.AddSeconds(1));

    private static QuotaLedgerPosition CreateQuotaPosition() => new(
        GroupId,
        PeriodId,
        new BigInteger(9_007_199_254_740_991L),
        new BigInteger(2_000),
        new BigInteger(1_500),
        new BigInteger(9_007_199_254_737_491L));

    private static TokenUsage ZeroUsage() => new(
        BigInteger.Zero,
        BigInteger.Zero,
        BigInteger.Zero,
        BigInteger.Zero,
        BigInteger.Zero);

    private static (TokenUsage Tokens, AttemptUsage Usage, AttemptUsageAdjustment Adjustment)
        CreateAdjustedUsage()
    {
        TokenUsage tokens = new(
            BigInteger.Parse(
                "123456789012345678901234567890",
                System.Globalization.CultureInfo.InvariantCulture),
            BigInteger.Parse(
                "987654321098765432109876543210",
                System.Globalization.CultureInfo.InvariantCulture),
            new BigInteger(200),
            new BigInteger(100),
            new BigInteger(300));
        AttemptUsage usage = new(
            tokens,
            SettlementUsageSource.Upstream,
            IsEstimated: false);
        TokenUsage correctedTokens = tokens with
        {
            OutputTokens = tokens.OutputTokens + 50,
        };
        AttemptUsageAdjustment adjustment = new(
            QuotaEventId,
            tokens.TotalTokens,
            correctedTokens,
            SettlementUsageSource.Upstream,
            new BigInteger(50),
            ObservedAt.AddMinutes(1));
        return (tokens, usage, adjustment);
    }

    private static EntityId Id(int suffix) => new(
        Guid.Parse($"018f3a4b-5c6d-7e8f-9123-{suffix:D12}"));
}
