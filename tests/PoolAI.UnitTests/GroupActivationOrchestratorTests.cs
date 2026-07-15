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
    public async Task ActivationCallsPortsInOrderAndForwardsOpaqueEvidence()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeUserStatusReader users = new(calls, Result.Success(
            new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 7, 3, ObservedAt)));
        FakeGroupStatusReader groups = new(calls, Result.Success(
            new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt)));
        FakeSupplyReadiness readiness = new(calls, Result.Success(
            new SupplyReadinessSnapshot(groupId, true, "v1.opaque-evidence", 29, ObservedAt)));
        FakeActivationCommand activation = new(calls, Result.Success(
            new GroupActivationResult(groupId, GroupLifecycle.Active, 12)));
        GroupActivationOrchestrator orchestrator = new(users, groups, readiness, activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            new GroupActivationRequest(
                new ActorContext(actorId, 7),
                groupId,
                11,
                "018f-idempotency",
                "Supply configuration verified"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(GroupLifecycle.Active, result.Value.Lifecycle);
        Assert.Equal(["identity", "group", "supply", "activate"], calls);
        ActivateGroupCommand command = Assert.IsType<ActivateGroupCommand>(activation.LastCommand);
        Assert.Equal("v1.opaque-evidence", command.SupplyEvidence.OpaqueToken);
        Assert.Equal(ObservedAt, command.SupplyEvidence.ObservedAt);
        Assert.Equal(11, command.ExpectedVersion);
    }

    [Fact]
    public async Task StaleActorTokenStopsBeforeGroupOrSupplyReads()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 8, 3, ObservedAt))),
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Failure<SupplyReadinessSnapshot>(
                "unexpected",
                "This port must not be called.")),
            new FakeActivationCommand(calls, Result.Failure<GroupActivationResult>(
                "unexpected",
                "This port must not be called.")));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            new GroupActivationRequest(
                new ActorContext(actorId, 7),
                groupId,
                11,
                "018f-idempotency",
                "Supply configuration verified"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(["identity"], calls);
    }

    [Fact]
    public async Task SupplyNotReadyDoesNotCallGroupMutation()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeActivationCommand activation = new(calls, Result.Failure<GroupActivationResult>(
            "unexpected",
            "This port must not be called."));
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 7, 3, ObservedAt))),
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, false, string.Empty, 29, ObservedAt))),
            activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            new GroupActivationRequest(
                new ActorContext(actorId, 7),
                groupId,
                11,
                "018f-idempotency",
                "Supply configuration verified"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_activation_not_ready", result.Error.Code);
        Assert.Equal(["identity", "group", "supply"], calls);
        Assert.Null(activation.LastCommand);
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
        GroupActivationOrchestrator orchestrator = CreateOrchestrator(calls, actorId, EntityId.New());

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
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, "v1.opaque-evidence", 29, ObservedAt))),
            new FakeActivationCommand(calls, Result.Success(
                new GroupActivationResult(groupId, GroupLifecycle.Active, 12))));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(["identity"], calls);
    }

    [Theory]
    [InlineData(12, GroupLifecycle.Disabled, true, "version_conflict")]
    [InlineData(11, GroupLifecycle.Active, true, "group_activation_not_ready")]
    [InlineData(11, GroupLifecycle.Disabled, false, "group_activation_not_ready")]
    public async Task ActivationRequiresMatchingDisabledGroupWithCurrentPeriod(
        long actualVersion,
        GroupLifecycle lifecycle,
        bool hasCurrentPeriod,
        string expectedError)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 7, 3, ObservedAt))),
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, lifecycle, actualVersion, hasCurrentPeriod, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, "v1.opaque-evidence", 29, ObservedAt))),
            new FakeActivationCommand(calls, Result.Success(
                new GroupActivationResult(groupId, GroupLifecycle.Active, 12))));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedError, result.Error.Code);
        Assert.Equal(["identity", "group"], calls);
    }

    [Theory]
    [InlineData("identity", "identity_unavailable")]
    [InlineData("group", "group_unavailable")]
    [InlineData("supply", "supply_unavailable")]
    public async Task PortFailureIsPropagatedAndStopsLaterEffects(
        string failingPort,
        string expectedError)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        Result<UserStatusSnapshot> userResult = string.Equals(
            failingPort,
            "identity",
            StringComparison.Ordinal)
            ? Result.Failure<UserStatusSnapshot>(expectedError, "Identity read failed.")
            : Result.Success(new UserStatusSnapshot(
                actorId,
                UserLifecycle.Active,
                SystemRole.Admin,
                7,
                3,
                ObservedAt));
        Result<GroupSnapshot> groupResult = string.Equals(
            failingPort,
            "group",
            StringComparison.Ordinal)
            ? Result.Failure<GroupSnapshot>(expectedError, "Group read failed.")
            : Result.Success(new GroupSnapshot(
                groupId,
                GroupLifecycle.Disabled,
                11,
                true,
                ObservedAt));
        Result<SupplyReadinessSnapshot> supplyResult = string.Equals(
            failingPort,
            "supply",
            StringComparison.Ordinal)
            ? Result.Failure<SupplyReadinessSnapshot>(expectedError, "Supply read failed.")
            : Result.Success(new SupplyReadinessSnapshot(
                groupId,
                true,
                "v1.opaque-evidence",
                29,
                ObservedAt));
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, userResult),
            new FakeGroupStatusReader(calls, groupResult),
            new FakeSupplyReadiness(calls, supplyResult),
            new FakeActivationCommand(calls, Result.Failure<GroupActivationResult>(
                "unexpected",
                "Mutation must not be called.")));

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedError, result.Error.Code);
        Assert.Equal(failingPort, calls[^1]);
        Assert.DoesNotContain("activate", calls);
    }

    [Fact]
    public async Task ReadyFlagWithoutEvidenceDoesNotAuthorizeMutation()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId groupId = EntityId.New();
        FakeActivationCommand activation = new(calls, Result.Failure<GroupActivationResult>(
            "unexpected",
            "Mutation must not be called."));
        GroupActivationOrchestrator orchestrator = new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 7, 3, ObservedAt))),
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, " ", 29, ObservedAt))),
            activation);

        Result<GroupActivationResult> result = await orchestrator.ActivateAsync(
            ValidRequest(actorId, groupId),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("group_activation_not_ready", result.Error.Code);
        Assert.Null(activation.LastCommand);
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
        EntityId groupId) => new(
            new FakeUserStatusReader(calls, Result.Success(
                new UserStatusSnapshot(actorId, UserLifecycle.Active, SystemRole.Admin, 7, 3, ObservedAt))),
            new FakeGroupStatusReader(calls, Result.Success(
                new GroupSnapshot(groupId, GroupLifecycle.Disabled, 11, true, ObservedAt))),
            new FakeSupplyReadiness(calls, Result.Success(
                new SupplyReadinessSnapshot(groupId, true, "v1.opaque-evidence", 29, ObservedAt))),
            new FakeActivationCommand(calls, Result.Success(
                new GroupActivationResult(groupId, GroupLifecycle.Active, 12))));

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

    private sealed class FakeGroupStatusReader(
        ICollection<string> calls,
        Result<GroupSnapshot> result) : IGroupStatusReader
    {
        public ValueTask<Result<GroupSnapshot>> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            calls.Add("group");
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
}
