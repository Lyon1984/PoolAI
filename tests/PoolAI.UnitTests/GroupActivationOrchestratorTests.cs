using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.UnitTests;

public sealed class GroupActivationOrchestratorTests
{
    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        7,
        15,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task ActivationProbesReplayBeforeSupplyAndForwardsOpaqueEvidence()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeActivationCommand activation = new(calls, Result.Success(
            new GroupActivationResult(groupId, GroupLifecycle.Active, 12)));
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            groupId,
            activation: activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(["identity", "idempotency", "supply", "activate"], calls);
        ActivateGroupCommand command = Assert.IsType<ActivateGroupCommand>(activation.LastCommand);
        SupplyReadinessEvidence evidence = Assert.IsType<SupplyReadinessEvidence>(
            command.SupplyEvidence);
        Assert.Equal("v1.opaque-evidence", evidence.OpaqueToken);
        Assert.Equal(ObservedAt, evidence.ObservedAt);
        Assert.Equal(11, command.ExpectedVersion);
    }

    [Fact]
    public async Task StaleActorTokenStopsBeforeIdempotencyAndSupply()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(
                    actorId,
                    UserLifecycle.Active,
                    SystemRole.Admin,
                    8,
                    3,
                    ObservedAt))),
            UnexpectedSupply(calls),
            UnexpectedActivation(calls),
            ProceedingPreflight(calls));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(["identity"], calls);
    }

    [Fact]
    public async Task SupplyNotReadyFlowsThroughFinalTransactionalCommand()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeActivationCommand activation = new(calls, Result.Failure<GroupActivationResult>(
            "group_activation_not_ready",
            "The Group does not satisfy its activation preconditions."));
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            groupId,
            supply: new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, false, string.Empty, 29, ObservedAt))),
            activation: activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_activation_not_ready", result.Error.Code);
        Assert.Equal(["identity", "idempotency", "supply", "activate"], calls);
        Assert.Null(Assert.IsType<ActivateGroupCommand>(activation.LastCommand).SupplyEvidence);
    }

    [Theory]
    [InlineData(0, "018f-idempotency", "Supply configuration verified", "validation_failed")]
    [InlineData(11, " ", "Supply configuration verified", "idempotency_key_required")]
    [InlineData(11, "018f-idempotency", " ", "validation_failed")]
    public async Task InvalidRequestStopsBeforeReadingAuthoritativeState(
        long expectedVersion,
        string idempotencyKey,
        string reason,
        string expectedError)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            EntityId.New());

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            new GroupActivationRequest(
                new ActorContext(actorId, 7),
                EntityId.New(),
                expectedVersion,
                idempotencyKey,
                reason),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedError, result.Error.Code);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(UserLifecycle.Disabled, SystemRole.Admin)]
    [InlineData(UserLifecycle.Active, SystemRole.Operator)]
    public async Task ActivationRequiresCurrentActiveAdministrator(
        UserLifecycle lifecycle,
        SystemRole role)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, lifecycle, role, 7, 3, ObservedAt))),
            UnexpectedSupply(calls),
            UnexpectedActivation(calls),
            ProceedingPreflight(calls));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(["identity"], calls);
    }

    [Theory]
    [InlineData("resource_not_found")]
    [InlineData("version_conflict")]
    [InlineData("group_activation_not_ready")]
    public async Task FinalCommandOwnsAndReturnsGroupPreconditionFailures(string errorCode)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            groupId,
            activation: new FakeActivationCommand(
                calls,
                Result.Failure<GroupActivationResult>(errorCode, "Final command rejected activation.")));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error.Code);
        Assert.Equal(["identity", "idempotency", "supply", "activate"], calls);
    }

    [Fact]
    public async Task TransientSupplyFailureDoesNotStartFinalCommand()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            groupId,
            supply: new FakeSupplyReadiness(calls, Result.Failure<SupplyReadinessSnapshot>(
                "dependency_unavailable",
                "Supply read failed.")),
            activation: UnexpectedActivation(calls));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(["identity", "idempotency", "supply"], calls);
    }

    [Fact]
    public async Task ReadyFlagWithoutEvidenceBecomesTransactionalNotReadyDecision()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeActivationCommand activation = new(calls, Result.Failure<GroupActivationResult>(
            "group_activation_not_ready",
            "Supply evidence is absent."));
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(
            calls,
            actorId,
            groupId,
            supply: new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, " ", 29, ObservedAt))),
            activation: activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_activation_not_ready", result.Error.Code);
        Assert.Null(Assert.IsType<ActivateGroupCommand>(activation.LastCommand).SupplyEvidence);
    }

    [Fact]
    public async Task CompletedReplayStopsBeforeSupplyAndFinalCommand()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationResult replay = new(
            groupId,
            GroupLifecycle.Active,
            12,
            new GroupResourceSnapshot(
                groupId,
                "Shared Pool",
                null,
                GroupLifecycle.Active,
                12,
                ObservedAt,
                ObservedAt));
        GroupActivationOrchestrator orchestrator = new(
            ActiveAdmin(calls, actorId),
            UnexpectedSupply(calls),
            UnexpectedActivation(calls),
            new FakeActivationPreflight(Result.Success<GroupActivationResult?>(replay), calls));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(replay, result.Value);
        Assert.Equal(["identity", "idempotency"], calls);
    }

    [Theory]
    [InlineData("group_activation_not_ready")]
    [InlineData("idempotency_conflict")]
    public async Task PreflightTerminalFailureStopsBeforeSupplyAndFinalCommand(string errorCode)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = new(
            ActiveAdmin(calls, actorId),
            UnexpectedSupply(calls),
            UnexpectedActivation(calls),
            new FakeActivationPreflight(
                Result.Failure<GroupActivationResult?>(errorCode, "Stored terminal result."),
                calls));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error.Code);
        Assert.Equal(["identity", "idempotency"], calls);
    }

    private static GroupActivationRequest ValidRequest(EntityId actorId, EntityId groupId) => new(
        new ActorContext(actorId, 7),
        groupId,
        11,
        "018f-idempotency",
        "Supply configuration verified");

    private static GroupActivationOrchestrator CreateOrchestrator(
        ICollection<string> calls,
        EntityId actorId,
        EntityId groupId,
        IGroupSupplyReadiness? supply = null,
        IGroupActivationCommand? activation = null) => new(
            ActiveAdmin(calls, actorId),
            supply ?? new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, "v1.opaque-evidence", 29, ObservedAt))),
            activation ?? new FakeActivationCommand(calls, Result.Success(
                new GroupActivationResult(groupId, GroupLifecycle.Active, 12))),
            ProceedingPreflight(calls));

    private static FakeUserStatusReader ActiveAdmin(
        ICollection<string> calls,
        EntityId actorId) =>
        new FakeUserStatusReader(calls, Result.Success(
            new UserStatusSnapshot(
                actorId,
                UserLifecycle.Active,
                SystemRole.Admin,
                7,
                3,
                ObservedAt)));

    private static FakeActivationPreflight ProceedingPreflight(
        ICollection<string> calls) => new FakeActivationPreflight(
            Result.Success<GroupActivationResult?>(null),
            calls);

    private static FakeSupplyReadiness UnexpectedSupply(ICollection<string> calls) =>
        new FakeSupplyReadiness(calls, Result.Failure<SupplyReadinessSnapshot>(
            "unexpected",
            "Supply must not be observed."));

    private static FakeActivationCommand UnexpectedActivation(ICollection<string> calls) => new(
        calls,
        Result.Failure<GroupActivationResult>(
            "unexpected",
            "The final activation command must not run."));

    private sealed class FakeUserStatusReader(
        ICollection<string> calls,
        Result<UserStatusSnapshot> result) : IUserStatusReader
    {
        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            calls.Add("identity");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeSupplyReadiness(
        ICollection<string> calls,
        Result<SupplyReadinessSnapshot> result) : IGroupSupplyReadiness
    {
        public ValueTask<Result<SupplyReadinessSnapshot>> ObserveAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            calls.Add("supply");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeActivationCommand(
        ICollection<string> calls,
        Result<GroupActivationResult> result) : IGroupActivationCommand
    {
        public ActivateGroupCommand? LastCommand { get; private set; }

        public ValueTask<Result<GroupActivationResult>> ActivateAsync(
            ActivateGroupCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("activate");
            LastCommand = command;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeActivationPreflight(
        Result<GroupActivationResult?> result,
        ICollection<string> calls) : IGroupActivationIdempotencyPreflight
    {
        public ValueTask<Result<GroupActivationResult?>> TryReplayAsync(
            GroupActivationOrchestrationCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("idempotency");
            return ValueTask.FromResult(result);
        }
    }
}
