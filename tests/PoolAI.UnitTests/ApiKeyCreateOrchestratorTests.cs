using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.UnitTests;

public sealed class ApiKeyCreateOrchestratorTests
{
    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        7,
        23,
        5,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task CompletedReplayPrecedesSubscriptionAndOwnerCommand()
    {
        List<string> calls = [];
        CreateApiKeyCommand command = ValidCommand();
        ApiKeyCreatedOutcome replay = Outcome(command, isReplay: true);
        ApiKeyCreateOrchestrator orchestrator = new(
            new FakePreflight(calls, Result.Success<ApiKeyCreatedOutcome?>(replay)),
            new FakeSubscriptionReader(
                calls,
                Result.Failure<SubscriptionAccessSnapshot>("unexpected", "Must not read.")),
            new FakeOwner(
                calls,
                Result.Failure<ApiKeyCreatedOutcome>("unexpected", "Must not write.")));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(replay, result.Value);
        Assert.Equal(["preflight"], calls);
    }

    [Fact]
    public async Task ActiveSubscriptionEvidenceFlowsToOwnerInFixedOrder()
    {
        List<string> calls = [];
        CreateApiKeyCommand command = ValidCommand();
        EntityId subscriptionId = EntityId.New();
        ApiKeyCreatedOutcome created = Outcome(command, isReplay: false);
        FakeOwner owner = new(calls, Result.Success(created));
        ApiKeyCreateOrchestrator orchestrator = new(
            ProceedingPreflight(calls),
            new FakeSubscriptionReader(calls, Result.Success(
                new SubscriptionAccessSnapshot(
                    subscriptionId,
                    command.UserId,
                    command.GroupId,
                    "standard",
                    ObservedAt.AddDays(-1),
                    ObservedAt.AddDays(30),
                    SubscriptionEffectiveStatus.Active,
                    7,
                    ObservedAt))),
            owner);

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(created, result.Value);
        Assert.Equal(["preflight", "subscription", "owner"], calls);
        ApiKeyAccessDecision decision = Assert.IsType<ApiKeyAccessDecision>(owner.LastDecision);
        Assert.Equal(ApiKeyAccessDecisionKind.Authorized, decision.Kind);
        Assert.Equal(command.UserId, decision.UserId);
        Assert.Equal(command.GroupId, decision.GroupId);
        Assert.Equal(subscriptionId, decision.SubscriptionId);
        Assert.Equal(ObservedAt, decision.ObservedAt);
    }

    [Theory]
    [InlineData("subscription_required", ApiKeyAccessDecisionKind.SubscriptionRequired)]
    [InlineData("subscription_inactive", ApiKeyAccessDecisionKind.SubscriptionInactive)]
    public async Task StableAccessDenialIsCompletedByIdentityOwner(
        string errorCode,
        ApiKeyAccessDecisionKind expectedDecision)
    {
        List<string> calls = [];
        CreateApiKeyCommand command = ValidCommand();
        FakeOwner owner = new(
            calls,
            Result.Failure<ApiKeyCreatedOutcome>(errorCode, "Stored access denial."));
        ApiKeyCreateOrchestrator orchestrator = new(
            ProceedingPreflight(calls),
            new FakeSubscriptionReader(
                calls,
                Result.Failure<SubscriptionAccessSnapshot>(errorCode, "Access denied.")),
            owner);

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error.Code);
        Assert.Equal(["preflight", "subscription", "owner"], calls);
        ApiKeyAccessDecision decision = Assert.IsType<ApiKeyAccessDecision>(owner.LastDecision);
        Assert.Equal(expectedDecision, decision.Kind);
        Assert.Null(decision.SubscriptionId);
        Assert.Null(decision.ObservedAt);
    }

    [Fact]
    public async Task TransientSubscriptionFailureDoesNotStartIdentityOwnerCommand()
    {
        List<string> calls = [];
        ApiKeyCreateOrchestrator orchestrator = new(
            ProceedingPreflight(calls),
            new FakeSubscriptionReader(
                calls,
                Result.Failure<SubscriptionAccessSnapshot>(
                    "dependency_unavailable",
                    "Subscription storage is unavailable.",
                    retryAfterSeconds: 3)),
            new FakeOwner(
                calls,
                Result.Failure<ApiKeyCreatedOutcome>("unexpected", "Must not write.")));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            ValidCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(3, result.Error.RetryAfterSeconds);
        Assert.Equal(["preflight", "subscription"], calls);
    }

    [Fact]
    public async Task InconsistentSubscriptionEvidenceFailsClosed()
    {
        List<string> calls = [];
        CreateApiKeyCommand command = ValidCommand();
        ApiKeyCreateOrchestrator orchestrator = new(
            ProceedingPreflight(calls),
            new FakeSubscriptionReader(calls, Result.Success(
                new SubscriptionAccessSnapshot(
                    EntityId.New(),
                    EntityId.New(),
                    command.GroupId,
                    "standard",
                    ObservedAt.AddDays(-1),
                    ObservedAt.AddDays(30),
                    SubscriptionEffectiveStatus.Active,
                    7,
                    ObservedAt))),
            new FakeOwner(
                calls,
                Result.Failure<ApiKeyCreatedOutcome>("unexpected", "Must not write.")));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(["preflight", "subscription"], calls);
    }

    [Fact]
    public async Task EmptySubscriptionIdentityFailsClosed()
    {
        List<string> calls = [];
        CreateApiKeyCommand command = ValidCommand();
        ApiKeyCreateOrchestrator orchestrator = new(
            ProceedingPreflight(calls),
            new FakeSubscriptionReader(calls, Result.Success(
                new SubscriptionAccessSnapshot(
                    default,
                    command.UserId,
                    command.GroupId,
                    "standard",
                    ObservedAt.AddDays(-1),
                    ObservedAt.AddDays(30),
                    SubscriptionEffectiveStatus.Active,
                    7,
                    ObservedAt))),
            new FakeOwner(
                calls,
                Result.Failure<ApiKeyCreatedOutcome>("unexpected", "Must not write.")));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, result.Error.RetryAfterSeconds);
        Assert.Equal(["preflight", "subscription"], calls);
    }

    [Theory]
    [InlineData("idempotency_conflict")]
    [InlineData("coordination_unavailable")]
    public async Task PreflightFailureStopsBeforeSubscription(string errorCode)
    {
        List<string> calls = [];
        ApiKeyCreateOrchestrator orchestrator = new(
            new FakePreflight(
                calls,
                Result.Failure<ApiKeyCreatedOutcome?>(errorCode, "Preflight failed.")),
            new FakeSubscriptionReader(
                calls,
                Result.Failure<SubscriptionAccessSnapshot>("unexpected", "Must not read.")),
            new FakeOwner(
                calls,
                Result.Failure<ApiKeyCreatedOutcome>("unexpected", "Must not write.")));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.CreateAsync(
            ValidCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error.Code);
        Assert.Equal(["preflight"], calls);
    }

    private static CreateApiKeyCommand ValidCommand()
    {
        EntityId userId = EntityId.New();
        return new CreateApiKeyCommand(
            EntityId.New(),
            new ApiKeyActor(userId, SystemRole.User, 4),
            ApiKeyAccessMode.Self,
            userId,
            EntityId.New(),
            "m1-e5-create-key",
            "CLI",
            ObservedAt.AddDays(90),
            ["10.0.0.0/24"],
            Reason: null,
            IpAddress: "192.0.2.10",
            UserAgent: "PoolAI.Test");
    }

    private static ApiKeyCreatedOutcome Outcome(
        CreateApiKeyCommand command,
        bool isReplay)
    {
        EntityId apiKeyId = EntityId.New();
        return new ApiKeyCreatedOutcome(
            201,
            isReplay,
            new ApiKeyControlPlaneSnapshot(
                apiKeyId,
                command.UserId,
                command.GroupId,
                command.Name,
                "sk-pool-AbCdEf12",
                ApiKeyPersistentStatus.Active,
                ApiKeyEffectiveStatus.Active,
                command.ExpiresAt,
                command.AllowedCidrs,
                LastUsedAt: null,
                Version: 1,
                CreatedAt: ObservedAt,
                UpdatedAt: ObservedAt,
                ObservedAt),
            "sk-pool-AbCdEf12abcdefghijklmnopqrstuvwxyz012345678",
            "\"v1\"",
            $"/api/v1/me/api-keys/{apiKeyId.Value:D}");
    }

    private static FakePreflight ProceedingPreflight(ICollection<string> calls) => new(
        calls,
        Result.Success<ApiKeyCreatedOutcome?>(null));

    private sealed class FakePreflight(
        ICollection<string> calls,
        Result<ApiKeyCreatedOutcome?> result) : IApiKeyCreateIdempotencyPreflight
    {
        public ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayAsync(
            CreateApiKeyCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("preflight");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeSubscriptionReader(
        ICollection<string> calls,
        Result<SubscriptionAccessSnapshot> result) : ISubscriptionAccessReader
    {
        public ValueTask<Result<SubscriptionAccessSnapshot>> GetEffectiveAccessAsync(
            EntityId userId,
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            calls.Add("subscription");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeOwner(
        ICollection<string> calls,
        Result<ApiKeyCreatedOutcome> result) : IApiKeyIssuer
    {
        public ApiKeyAccessDecision? LastDecision { get; private set; }

        public ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
            CreateApiKeyCommand command,
            ApiKeyAccessDecision accessDecision,
            CancellationToken cancellationToken)
        {
            calls.Add("owner");
            LastDecision = accessDecision;
            return ValueTask.FromResult(result);
        }
    }
}
