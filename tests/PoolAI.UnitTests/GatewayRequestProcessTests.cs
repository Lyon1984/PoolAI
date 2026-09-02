using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

// Governing contracts:
// - ADR 0015, canonical admission -> one M4-E1 attempt.
// - docs/architecture/design-pattern-baseline.md, GatewayRequestProcess.
// - docs/开发执行规格-v1.0.md, D-004/DEC-022 and M4-E1.
public sealed class GatewayRequestProcessTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        9,
        2,
        12,
        0,
        0,
        TimeSpan.Zero);

    private static readonly EntityId ApiKeyId =
        Id("01920000-0000-7000-8000-000000000001");
    private static readonly EntityId UserId =
        Id("01920000-0000-7000-8000-000000000002");
    private static readonly EntityId GroupId =
        Id("01920000-0000-7000-8000-000000000003");
    private static readonly EntityId SubscriptionId =
        Id("01920000-0000-7000-8000-000000000004");
    private static readonly EntityId RequestId =
        Id("01920000-0000-7000-8000-000000000009");

    [Fact]
    public async Task CanonicalAdmissionAndInboundRpmPrecedeBodyValidation()
    {
        List<string> events = [];
        RecordingRateLimiter rateLimiter = new(events);
        RecordingSingleAttemptExecutor executor = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);

        Result<GatewayAuthorizedRequest> authorized =
            await process.AuthorizeAsync(
                "poolai-sensitive-key",
                IPAddress.Parse("203.0.113.10"),
                forwardedForFieldValues: null,
                TestContext.Current.CancellationToken);
        Assert.True(authorized.IsSuccess);

        events.Add("body-authoritative-validation");
        Result<GatewaySingleAttemptOutcome> result = await process
            .ExecuteInitialAttemptAsync(
                authorized.Value,
                InboundProtocol.Responses,
                Request(),
                clientRequestId: "client-request",
                Now.AddMinutes(2),
                sessionAffinityHash: "affinity-hash",
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("single_attempt_observed", result.Error.Code);
        Assert.Equal(1, rateLimiter.Calls);
        Assert.Equal(
            [
                "api-key",
                "user",
                "subscription",
                "group",
                "rpm",
                "body-authoritative-validation",
                "single-attempt",
            ],
            events);
    }

    [Fact]
    public async Task CanonicalSuccessCountsRpmWhenBodyValidationStopsExecution()
    {
        List<string> events = [];
        RecordingRateLimiter rateLimiter = new(events);
        RecordingSingleAttemptExecutor executor = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);

        Result<GatewayAuthorizedRequest> authorized = await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken);
        events.Add("body-authoritative-validation-rejected");

        Assert.True(authorized.IsSuccess);
        Assert.Equal(1, rateLimiter.Calls);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(
            [
                "api-key",
                "user",
                "subscription",
                "group",
                "rpm",
                "body-authoritative-validation-rejected",
            ],
            events);
    }

    [Fact]
    public async Task RpmFailureFailsClosedWithoutIssuingCapability()
    {
        List<string> events = [];
        RecordingRateLimiter rateLimiter = new(events) { Fail = true };
        RecordingSingleAttemptExecutor executor = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);

        Result<GatewayAuthorizedRequest> result = await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_rate_limited", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(1, rateLimiter.Calls);
        Assert.Equal(0, executor.Calls);
        Assert.Equal(
            ["api-key", "user", "subscription", "group", "rpm"],
            events);
    }

    [Fact]
    public async Task BuildsOneFixedInitialAttemptFromSanitizedInput()
    {
        List<string> events = [];
        RecordingSingleAttemptExecutor executor = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            new RecordingRateLimiter(events),
            executor);
        DateTimeOffset deadline = Now.AddMinutes(2);
        List<string> forwarded = ["203.0.113.10"];
        IPAddress socketPeer = IPAddress.Parse("198.51.100.20");
        Result<GatewayAuthorizedRequest> authorized =
            await process.AuthorizeAsync(
                "poolai-sensitive-key",
                socketPeer,
                forwarded,
                TestContext.Current.CancellationToken);
        Assert.True(authorized.IsSuccess);

        Result<GatewaySingleAttemptOutcome> result = await process
            .ExecuteInitialAttemptAsync(
                authorized.Value,
                InboundProtocol.Responses,
                Request(),
                clientRequestId: "client-request",
                deadline,
                sessionAffinityHash: "affinity-hash",
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("single_attempt_observed", result.Error.Code);
        Assert.Equal(
            [
                "api-key",
                "user",
                "subscription",
                "group",
                "rpm",
                "single-attempt",
            ],
            events);
        Assert.Equal(1, executor.Calls);
        GatewaySingleAttemptRequest captured = Assert.IsType<
            GatewaySingleAttemptRequest>(executor.Command);
        Assert.Equal(0, captured.AttemptIndex);
        Assert.Equal(0, captured.RemainingRetryBudget);
        Assert.Equal(deadline, captured.Deadline);
        Assert.Equal(RequestId, captured.Request.RequestId);
        Assert.Equal(7, captured.Request.RequestId.Value.Version);
        Assert.Equal(InboundProtocol.Responses, captured.Protocol);
        Assert.Equal("client-request", captured.ClientRequestId);
        Assert.Equal("affinity-hash", captured.SessionAffinityHash);
        Assert.Equal(
            "gateway:01920000000070008000000000000009:0",
            captured.LeaseOwner);
        Assert.Equal(ApiKeyId, captured.Access.ApiKey.ApiKeyId);
        Assert.Equal(UserId, captured.Access.User.UserId);
        Assert.Equal(SubscriptionId,
            captured.Access.Subscription.SubscriptionId);
        Assert.Equal(GroupId, captured.Access.Group.GroupId);
    }

    [Fact]
    public async Task AuthorizationSnapshotsForwardedValuesBeforeItsFirstAwait()
    {
        List<string> events = [];
        BlockingApiKeyAuthenticator authenticator = new(events);
        GatewayCanonicalAdmissionService canonical = new(
            authenticator,
            new FakeUserReader(events),
            new FakeSubscriptionReader(events),
            new FakeGroupReader(events),
            new GatewayClientIpResolver(new GatewayIngressOptions(
                ["198.51.100.0/24"])));
        GatewayRequestProcess process = new(
            canonical,
            new RecordingRateLimiter(events),
            new RecordingSingleAttemptExecutor(events));
        List<string> forwarded = ["203.0.113.10"];

        ValueTask<Result<GatewayAuthorizedRequest>> pending =
            process.AuthorizeAsync(
                "poolai-sensitive-key",
                IPAddress.Parse("198.51.100.20"),
                forwarded,
                TestContext.Current.CancellationToken);
        await authenticator.Entered.ConfigureAwait(true);
        forwarded[0] = "192.0.2.99";
        authenticator.Release();
        Result<GatewayAuthorizedRequest> result =
            await pending.ConfigureAwait(true);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CanonicalFailureNeverConstructsOrExecutesAnAttempt()
    {
        List<string> events = [];
        RecordingSingleAttemptExecutor executor = new(events);
        GatewayCanonicalAdmissionService canonical = Canonical(
            events,
            apiKeyFailure: Result.Failure<ApiKeyAccessSnapshot>(
                "invalid_api_key",
                "The API Key is invalid."));
        RecordingRateLimiter rateLimiter = new(events);
        GatewayRequestProcess process = new(canonical, rateLimiter, executor);

        Result<GatewayAuthorizedRequest> result = await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_api_key", result.Error.Code);
        Assert.Equal(["api-key"], events);
        Assert.Equal(0, rateLimiter.Calls);
        Assert.Equal(0, executor.Calls);
        Assert.Null(executor.Command);
    }

    [Fact]
    public async Task AuthorizationCapabilityHasNoPublicCanonicalOrRawSecretSurface()
    {
        List<string> events = [];
        GatewayRequestProcess process = new(
            Canonical(events),
            new RecordingRateLimiter(events),
            new RecordingSingleAttemptExecutor(events));

        Result<GatewayAuthorizedRequest> result = await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("198.51.100.20"),
            ["203.0.113.10"],
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        GatewayAuthorizedRequest authorization = result.Value;
        Type capabilityType = typeof(GatewayAuthorizedRequest);

        Assert.Empty(capabilityType.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(capabilityType.GetFields(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(capabilityType.GetProperties(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            capabilityType.GetMethods(
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.DeclaredOnly),
            static method => method.ReturnType == typeof(GatewayCanonicalAccess)
                || method.GetParameters().Any(static parameter =>
                    parameter.ParameterType == typeof(GatewayCanonicalAccess)));
        Assert.Equal(nameof(GatewayAuthorizedRequest), authorization.ToString());
        Assert.DoesNotContain(
            "poolai-sensitive-key",
            authorization.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "203.0.113.10",
            authorization.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityIsConsumedByTheFirstExecutionEvenWhenItFails()
    {
        List<string> events = [];
        RecordingSingleAttemptExecutor executor = new(events);
        RecordingRateLimiter rateLimiter = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);
        GatewayAuthorizedRequest authorization = (await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken)).Value;

        Result<GatewaySingleAttemptOutcome> first = await ExecuteAsync(
            process,
            authorization,
            TestContext.Current.CancellationToken);
        Result<GatewaySingleAttemptOutcome> replay = await ExecuteAsync(
            process,
            authorization,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal("single_attempt_observed", first.Error.Code);
        Assert.True(replay.IsFailure);
        Assert.Equal("invalid_request", replay.Error.Code);
        Assert.Equal(1, rateLimiter.Calls);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task CapabilityRejectsAnotherProcessWithoutConsumingOwnerUse()
    {
        List<string> ownerEvents = [];
        List<string> otherEvents = [];
        RecordingSingleAttemptExecutor ownerExecutor = new(ownerEvents);
        RecordingSingleAttemptExecutor otherExecutor = new(otherEvents);
        RecordingRateLimiter ownerRateLimiter = new(ownerEvents);
        RecordingRateLimiter otherRateLimiter = new(otherEvents);
        GatewayRequestProcess owner = new(
            Canonical(ownerEvents),
            ownerRateLimiter,
            ownerExecutor);
        GatewayRequestProcess other = new(
            Canonical(otherEvents),
            otherRateLimiter,
            otherExecutor);
        GatewayAuthorizedRequest authorization = (await owner.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken)).Value;

        Result<GatewaySingleAttemptOutcome> rejected = await ExecuteAsync(
            other,
            authorization,
            TestContext.Current.CancellationToken);
        Result<GatewaySingleAttemptOutcome> accepted = await ExecuteAsync(
            owner,
            authorization,
            TestContext.Current.CancellationToken);

        Assert.True(rejected.IsFailure);
        Assert.Equal("invalid_request", rejected.Error.Code);
        Assert.Equal(0, otherExecutor.Calls);
        Assert.True(accepted.IsFailure);
        Assert.Equal("single_attempt_observed", accepted.Error.Code);
        Assert.Equal(1, ownerRateLimiter.Calls);
        Assert.Equal(0, otherRateLimiter.Calls);
        Assert.Equal(1, ownerExecutor.Calls);
    }

    [Fact]
    public async Task CancelledExecutionConsumesCapabilityWithoutStartingAttempt()
    {
        List<string> events = [];
        RecordingSingleAttemptExecutor executor = new(events);
        RecordingRateLimiter rateLimiter = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);
        GatewayAuthorizedRequest authorization = (await process.AuthorizeAsync(
            "poolai-sensitive-key",
            IPAddress.Parse("203.0.113.10"),
            forwardedForFieldValues: null,
            TestContext.Current.CancellationToken)).Value;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ExecuteAsync(process, authorization, cancellation.Token)
                .ConfigureAwait(false));
        Result<GatewaySingleAttemptOutcome> replay = await ExecuteAsync(
            process,
            authorization,
            TestContext.Current.CancellationToken);

        Assert.True(replay.IsFailure);
        Assert.Equal("invalid_request", replay.Error.Code);
        Assert.Equal(1, rateLimiter.Calls);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task CancelledAuthorizationDoesNotIssueCapabilityOrStartAttempt()
    {
        List<string> events = [];
        RecordingSingleAttemptExecutor executor = new(events);
        RecordingRateLimiter rateLimiter = new(events);
        GatewayRequestProcess process = new(
            Canonical(events),
            rateLimiter,
            executor);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await process.AuthorizeAsync(
                "poolai-sensitive-key",
                IPAddress.Parse("203.0.113.10"),
                forwardedForFieldValues: null,
                cancellation.Token).ConfigureAwait(false));

        Assert.Empty(events);
        Assert.Equal(0, rateLimiter.Calls);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void ProductionCompositionRegistersTheRequestProcessOnce()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = [];
        List<string> events = [];

        services.AddGatewayModule(
            configuration,
            environmentName: "Test",
            disconnectDrainSeconds: 15);
        services.AddSingleton<IApiKeyAuthenticator>(
            new FakeApiKeyAuthenticator(events));
        services.AddSingleton<IUserStatusReader>(new FakeUserReader(events));
        services.AddSingleton<ISubscriptionAccessReader>(
            new FakeSubscriptionReader(events));
        services.AddSingleton<IGroupStatusReader>(new FakeGroupReader(events));
        services.AddSingleton<IGroupRequestRateLimiter>(
            new RecordingRateLimiter(events));
        services.AddSingleton<IAccountRouter, UnexpectedAccountRouter>();
        services.AddSingleton<IGroupQuotaLedger, UnexpectedQuotaLedger>();
        services.AddSingleton<IRouteCredentialLeaseSource,
            UnexpectedCredentialSource>();

        ServiceDescriptor registration = Assert.Single(
            services,
            static descriptor =>
                descriptor.ServiceType == typeof(GatewayRequestProcess));
        Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
        Assert.NotNull(registration.ImplementationFactory);
        Assert.DoesNotContain(
            services,
            static descriptor => descriptor.ServiceType
                == typeof(GatewaySingleAttemptProcessManager));
        Assert.Empty(typeof(GatewayRequestProcess).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        GatewayRequestProcess first = provider.GetRequiredService<
            GatewayRequestProcess>();
        Assert.Same(
            first,
            provider.GetRequiredService<GatewayRequestProcess>());
    }

    private static NormalizedGatewayRequest Request() => new(
        RequestId,
        "gpt-m4-e1",
        Stream: false,
        JsonSerializer.SerializeToElement(new
        {
            model = "gpt-m4-e1",
            input = "sensitive model input",
            max_output_tokens = 64,
        }));

    private static ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
        GatewayRequestProcess process,
        GatewayAuthorizedRequest authorization,
        CancellationToken cancellationToken) =>
        process.ExecuteInitialAttemptAsync(
            authorization,
            InboundProtocol.Responses,
            Request(),
            clientRequestId: "client-request",
            Now.AddMinutes(2),
            sessionAffinityHash: "affinity-hash",
            cancellationToken);

    private static GatewayCanonicalAdmissionService Canonical(
        List<string> events,
        Result<ApiKeyAccessSnapshot>? apiKeyFailure = null) => new(
        new FakeApiKeyAuthenticator(events, apiKeyFailure),
        new FakeUserReader(events),
        new FakeSubscriptionReader(events),
        new FakeGroupReader(events),
        new GatewayClientIpResolver(new GatewayIngressOptions()));

    private static EntityId Id(string value) => new(Guid.Parse(value));

    private sealed class RecordingSingleAttemptExecutor(List<string> events) :
        IGatewaySingleAttemptExecutor
    {
        internal int Calls { get; private set; }

        internal GatewaySingleAttemptRequest? Command { get; private set; }

        public ValueTask<Result<GatewaySingleAttemptOutcome>> ExecuteAsync(
            GatewaySingleAttemptRequest command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("single-attempt");
            Calls++;
            Command = command;
            return ValueTask.FromResult(Result.Failure<
                GatewaySingleAttemptOutcome>(
                "single_attempt_observed",
                "The initial attempt was captured."));
        }
    }

    private sealed class FakeApiKeyAuthenticator(
        List<string> events,
        Result<ApiKeyAccessSnapshot>? failure = null) : IApiKeyAuthenticator
    {
        public ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
            string presentedKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("api-key");
            Assert.Equal("poolai-sensitive-key", presentedKey);
            return ValueTask.FromResult(failure ?? Result.Success(
                new ApiKeyAccessSnapshot(
                    ApiKeyId,
                    UserId,
                    GroupId,
                    IsEffective: true,
                    AllowedCidrs: [],
                    Version: 2,
                    ObservedAt: Now)));
        }
    }

    private sealed class BlockingApiKeyAuthenticator(List<string> events) :
        IApiKeyAuthenticator
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _release.TrySetResult();

        public async ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
            string presentedKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("api-key");
            Assert.Equal("poolai-sensitive-key", presentedKey);
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(new ApiKeyAccessSnapshot(
                ApiKeyId,
                UserId,
                GroupId,
                IsEffective: true,
                AllowedCidrs: ["203.0.113.10/32"],
                Version: 2,
                ObservedAt: Now));
        }
    }

    private sealed class FakeUserReader(List<string> events) : IUserStatusReader
    {
        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("user");
            Assert.Equal(UserId, userId);
            return ValueTask.FromResult(Result.Success(new UserStatusSnapshot(
                UserId,
                UserLifecycle.Active,
                SystemRole.User,
                TokenVersion: 3,
                Version: 4,
                ObservedAt: Now)));
        }
    }

    private sealed class FakeSubscriptionReader(List<string> events) :
        ISubscriptionAccessReader
    {
        public ValueTask<Result<SubscriptionAccessSnapshot>>
            GetEffectiveAccessAsync(
                EntityId userId,
                EntityId groupId,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("subscription");
            Assert.Equal(UserId, userId);
            Assert.Equal(GroupId, groupId);
            return ValueTask.FromResult(Result.Success(
                new SubscriptionAccessSnapshot(
                    SubscriptionId,
                    UserId,
                    GroupId,
                    "standard",
                    Now.AddDays(-1),
                    Now.AddDays(1),
                    SubscriptionEffectiveStatus.Active,
                    Version: 5,
                    ObservedAt: Now)));
        }
    }

    private sealed class FakeGroupReader(List<string> events) :
        IGroupStatusReader
    {
        public ValueTask<Result<GroupSnapshot>> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("group");
            Assert.Equal(GroupId, groupId);
            return ValueTask.FromResult(Result.Success(new GroupSnapshot(
                GroupId,
                GroupLifecycle.Active,
                Version: 6,
                HasCurrentQuotaPeriod: true,
                ObservedAt: Now,
                RequestsPerMinute: 6_000)));
        }
    }

    private sealed class RecordingRateLimiter(List<string> events) :
        IGroupRequestRateLimiter
    {
        internal bool Fail { get; set; }

        internal int Calls { get; private set; }

        public ValueTask<Result<GroupRequestRateLimitPermit>> AcquireAsync(
            EntityId groupId,
            int requestsPerMinute,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Add("rpm");
            Calls++;
            Assert.Equal(GroupId, groupId);
            Assert.Equal(6_000, requestsPerMinute);
            return ValueTask.FromResult(Fail
                ? Result.Failure<GroupRequestRateLimitPermit>(
                    "group_rate_limited",
                    "The Group RPM limit was reached.",
                    retryAfterSeconds: 1)
                : Result.Success(new GroupRequestRateLimitPermit(1)));
        }
    }

    private sealed class UnexpectedAccountRouter : IAccountRouter
    {
        public ValueTask<Result<IAccountLease>> RouteAsync(
            RouteAccountCommand command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Routing must not run after the RPM rejection.");
    }

    private sealed class UnexpectedQuotaLedger : IGroupQuotaLedger
    {
        public ValueTask<Result<ReserveQuotaResult>> ReserveAsync(
            ReserveQuotaCommand command,
            CancellationToken cancellationToken) => Unexpected<ReserveQuotaResult>();

        public ValueTask<Result<DispatchedReservationHandle>> MarkDispatchedAsync(
            MarkReservationDispatchedCommand command,
            CancellationToken cancellationToken) =>
            Unexpected<DispatchedReservationHandle>();

        public ValueTask<Result<ReservationHandle>> RenewAsync(
            RenewReservationCommand command,
            CancellationToken cancellationToken) => Unexpected<ReservationHandle>();

        public ValueTask<Result<QuotaTransitionResult>> SettleAsync(
            SettleReservationCommand command,
            CancellationToken cancellationToken) => Unexpected<QuotaTransitionResult>();

        public ValueTask<Result<QuotaTransitionResult>> ReleaseAsync(
            ReleaseReservationCommand command,
            CancellationToken cancellationToken) => Unexpected<QuotaTransitionResult>();

        private static ValueTask<Result<T>> Unexpected<T>() =>
            throw new InvalidOperationException(
                "Quota must not run after the RPM rejection.");
    }

    private sealed class UnexpectedCredentialSource :
        IRouteCredentialLeaseSource
    {
        public ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
            RouteCredentialLeaseRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Credential acquisition must not run after the RPM rejection.");
    }

    private sealed class UnexpectedTransport : IGatewayUpstreamTransport
    {
        public ValueTask<GatewayUpstreamTransportResult> SendAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            AdapterAttemptContext attemptContext,
            AdapterCapability capability,
            IUpstreamCredentialHandle credential,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Transport must not run after the RPM rejection.");
    }
}
