using System.Numerics;
using System.Reflection;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Routing.Abstractions;

namespace PoolAI.UnitTests;

// Governing contract: design-pattern-baseline.md section 8, AttemptContext is
// Gateway-owned and phase/evidence/final disposition advance monotonically.
public sealed class GatewayAttemptLifecycleTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        2,
        14,
        0,
        0,
        TimeSpan.Zero);

    private static readonly EntityId RequestId =
        Id("01920000-0000-7000-8000-000000000101");
    private static readonly EntityId AttemptId =
        Id("01920000-0000-7000-8000-000000000102");
    private static readonly EntityId GroupId =
        Id("01920000-0000-7000-8000-000000000103");
    private static readonly EntityId ChannelId =
        Id("01920000-0000-7000-8000-000000000104");
    private static readonly EntityId AccountId =
        Id("01920000-0000-7000-8000-000000000105");
    private static readonly EntityId ReservationId =
        Id("01920000-0000-7000-8000-000000000106");
    private static readonly EntityId PeriodId =
        Id("01920000-0000-7000-8000-000000000107");

    [Fact]
    public void AdapterViewCannotBePubliclyConstructedOrMutated()
    {
        Type context = typeof(AdapterAttemptContext);

        Assert.Empty(context.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            context.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly),
            static method => method.Name.StartsWith(
                "Mark",
                StringComparison.Ordinal)
                || method.Name.StartsWith("Advance", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.GetProperties(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly),
            static property => property.PropertyType
                == typeof(IGatewayAttemptOutputEvidenceSink));
    }

    [Fact]
    public void LifecycleOwnsResourcesAndCapturesUsageEvidenceAndDisposition()
    {
        FakeAccountLease lease = new();
        GatewayAttemptLifecycle lifecycle = CreateLifecycle(lease);
        ReservationHandle reservation = Reservation();
        lifecycle.BindReservation(reservation);
        lifecycle.MarkDispatchFenceCommitted(Dispatched(reservation));

        lifecycle.AdapterContext.OutputEvidenceSink
            .MarkDownstreamHeadersCommitted();
        lifecycle.AdapterContext.OutputEvidenceSink
            .MarkBusinessOutputStarted();
        NormalizedUpstreamUsage usage = new(
            new BigInteger(31),
            new BigInteger(17),
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            JsonSerializer.SerializeToElement(new { input_tokens = 31 }));
        lifecycle.RecordTransportResult(new GatewayUpstreamTransportResult(
            Result.Success(new NormalizedUpstreamResult(
                200,
                JsonSerializer.SerializeToElement(new { id = "response" }),
                usage,
                ErrorCode: null)),
            GatewayRequestWriteEvidence.ConfirmedWritten,
            ConfirmedNoExecution: false));
        lifecycle.Complete(GatewaySingleAttemptDisposition.Succeeded);

        Assert.Equal(GroupId, lifecycle.QuotaGroupId);
        Assert.Equal(GroupId, lifecycle.RoutingGroupId);
        Assert.Same(lease, lifecycle.AccountLease);
        Assert.Same(reservation, lifecycle.Reservation);
        Assert.Equal(
            GatewayAttemptPhase.BusinessOutputStarted,
            lifecycle.Phase);
        Assert.True(lifecycle.AdapterContext.RequestBytesWritten);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            lifecycle.Evidence.RequestWriteEvidence);
        Assert.Same(usage, lifecycle.Evidence.Usage);
        Assert.Null(lifecycle.Evidence.TransportErrorCode);
        Assert.False(lifecycle.Evidence.ConfirmedNoExecution);
        Assert.Equal(
            GatewaySingleAttemptDisposition.Succeeded,
            lifecycle.FinalDisposition);
    }

    [Fact]
    public void IllegalPhaseSkipAndPostTerminalMutationFailClosed()
    {
        GatewayAttemptLifecycle lifecycle = CreateLifecycle(
            new FakeAccountLease());
        ReservationHandle reservation = Reservation();
        lifecycle.BindReservation(reservation);
        lifecycle.MarkDispatchFenceCommitted(Dispatched(reservation));

        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.AdapterContext.OutputEvidenceSink
                .MarkBusinessOutputStarted());
        Assert.Equal(
            GatewayAttemptPhase.DispatchedNoDownstreamHeaders,
            lifecycle.Phase);

        lifecycle.Complete(GatewaySingleAttemptDisposition.Failed);
        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.AdapterContext.OutputEvidenceSink
                .MarkDownstreamHeadersCommitted());
        Assert.Throws<InvalidOperationException>(() => lifecycle.Complete(
            GatewaySingleAttemptDisposition.Cancelled));
        Assert.Equal(
            GatewaySingleAttemptDisposition.Failed,
            lifecycle.FinalDisposition);
    }

    [Fact]
    public async Task ConcurrentSameStageEvidenceAdvancesExactlyOnce()
    {
        GatewayAttemptLifecycle lifecycle = CreateLifecycle(
            new FakeAccountLease());
        ReservationHandle reservation = Reservation();
        lifecycle.BindReservation(reservation);
        lifecycle.MarkDispatchFenceCommitted(Dispatched(reservation));

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            lifecycle.AdapterContext.OutputEvidenceSink
                .MarkDownstreamHeadersCommitted())));
        Assert.Equal(
            GatewayAttemptPhase.DownstreamHeadersCommitted,
            lifecycle.Phase);

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            lifecycle.AdapterContext.OutputEvidenceSink
                .MarkBusinessOutputStarted())));
        Assert.Equal(
            GatewayAttemptPhase.BusinessOutputStarted,
            lifecycle.Phase);
    }

    [Fact]
    public void ReservationFromAnotherQuotaGroupCannotBeBound()
    {
        GatewayAttemptLifecycle lifecycle = CreateLifecycle(
            new FakeAccountLease());

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() => lifecycle.BindReservation(
                Reservation() with
                {
                    GroupId = Id(
                        "01920000-0000-7000-8000-000000000108"),
                }));

        Assert.Contains(
            "not owned",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Null(lifecycle.Reservation);
    }

    [Fact]
    public void InternalAttemptExecutionTypesAreNotExportedOrCallable()
    {
        Type[] hiddenTypes =
        [
            typeof(GatewayCanonicalAccess),
            typeof(GatewaySingleAttemptRequest),
            typeof(GatewayCanonicalAdmissionService),
            typeof(GatewaySingleAttemptProcessManager),
            typeof(IGatewaySingleAttemptExecutor),
            typeof(GatewaySingleAttemptExecutor),
            typeof(IGatewayUpstreamTransport),
            typeof(GatewayCredentialHandoff),
        ];
        Type[] exported = typeof(GatewayRequestProcess).Assembly
            .GetExportedTypes();

        Assert.All(hiddenTypes, type =>
        {
            Assert.False(type.IsPublic || type.IsNestedPublic);
            Assert.DoesNotContain(type, exported);
        });
        Assert.Empty(typeof(GatewayCanonicalAccess).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GatewaySingleAttemptRequest).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GatewaySingleAttemptProcessManager).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GatewaySingleAttemptProcessManager).GetMethods(
            BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.DeclaredOnly));
        Assert.Contains(
            typeof(GatewayRequestProcess),
            exported);
    }

    private static GatewayAttemptLifecycle CreateLifecycle(
        IAccountLease lease) => new(
            RequestId,
            AttemptId,
            attemptIndex: 0,
            GroupId,
            GroupId,
            AdapterRoute(),
            lease,
            Now.AddMinutes(2),
            remainingRetryBudget: 0);

    private static ReservationHandle Reservation() => new(
        ReservationId,
        RequestId,
        AttemptId,
        AttemptIndex: 0,
        GroupId,
        PeriodId,
        AccountId,
        ChannelId,
        EstimatedTokens: 128,
        IsStreaming: false,
        LeaseOwner: "gateway:test:0",
        Now.AddMinutes(5),
        Now.AddMinutes(10));

    private static DispatchedReservationHandle Dispatched(
        ReservationHandle reservation) => new(
            ReservationStatus.Pending,
            reservation,
            SettlementProvider.OpenAi,
            "gpt-upstream",
            new TokenEstimateSplit(64, 64),
            Now);

    private static AdapterRouteSnapshot AdapterRoute() => new(
        GroupId,
        ChannelId,
        AccountId,
        UpstreamType.OpenAi,
        "gpt-client",
        "gpt-upstream",
        new Uri("https://api.example.test/v1/"),
        SupportsResponses: true,
        SupportsChatCompletions: true,
        SupportsFunctionTools: true,
        SupportsStreaming: true,
        SupplyConfigurationVersion: 3,
        ChannelVersion: 4,
        AccountVersion: 5,
        CredentialRevision: 6);

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed class FakeAccountLease : IAccountLease
    {
        public AccountRoute Route { get; } = new(
            GroupId,
            ChannelId,
            AccountId,
            AccountRouteProvider.OpenAi,
            "gpt-client",
            "gpt-upstream",
            new Uri("https://api.example.test/v1/"),
            new AccountRouteCapabilities(true, true, true, true),
            Now.AddMinutes(1),
            SupplyConfigurationVersion: 3,
            ChannelVersion: 4,
            AccountVersion: 5,
            CredentialRevision: 6);

        public ValueTask<AccountLeaseRenewResult> RenewAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(AccountLeaseRenewResult.Renewed(Route));

        public ValueTask<Result<bool>> ReleaseAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success(true));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
