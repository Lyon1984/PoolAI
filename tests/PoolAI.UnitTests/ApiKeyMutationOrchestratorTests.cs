using PoolAI.Application.Orchestration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.UnitTests;

public sealed class ApiKeyMutationOrchestratorTests
{
    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        7,
        23,
        6,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task UpdateReplayPrecedesResourceReadAndSubscriptionGate()
    {
        List<string> calls = [];
        UpdateApiKeyCommand command = MetadataUpdateCommand();
        ApiKeyUpdatedOutcome replay = UpdatedOutcome(
            Snapshot(command.UserId, command.ApiKeyId),
            isReplay: true);
        FakePreflight preflight = new(calls)
        {
            UpdateResult = Result.Success<ApiKeyUpdatedOutcome?>(replay),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            preflight,
            ReaderThatMustNotRun(calls),
            SubscriptionThatMustNotRun(calls),
            OwnerThatMustNotRun(calls));

        Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(replay, result.Value);
        Assert.Equal(["preflight:update"], calls);
    }

    [Fact]
    public async Task RevokeReplayPrecedesResourceRead()
    {
        List<string> calls = [];
        RevokeApiKeyCommand command = RevokeCommand();
        ApiKeyRevokedOutcome replay = new(204, IsReplay: true, "\"v8\"");
        FakePreflight preflight = new(calls)
        {
            RevokeResult = Result.Success<ApiKeyRevokedOutcome?>(replay),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            preflight,
            ReaderThatMustNotRun(calls),
            SubscriptionThatMustNotRun(calls),
            OwnerThatMustNotRun(calls));

        Result<ApiKeyRevokedOutcome> result = await orchestrator.RevokeAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(replay, result.Value);
        Assert.Equal(["preflight:revoke"], calls);
    }

    [Fact]
    public async Task RotateReplayPrecedesResourceReadAndSubscriptionGate()
    {
        List<string> calls = [];
        RotateApiKeyCommand command = RotateCommand();
        ApiKeyCreatedOutcome replay = CreatedOutcome(
            command.UserId,
            EntityId.New(),
            isReplay: true);
        FakePreflight preflight = new(calls)
        {
            RotateResult = Result.Success<ApiKeyCreatedOutcome?>(replay),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            preflight,
            ReaderThatMustNotRun(calls),
            SubscriptionThatMustNotRun(calls),
            OwnerThatMustNotRun(calls));

        Result<ApiKeyCreatedOutcome> result = await orchestrator.RotateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(replay, result.Value);
        Assert.Equal(["preflight:rotate"], calls);
    }

    [Fact]
    public async Task RevokedUpdateSkipsSubscriptionAndLetsIdentityOwnerDecide()
    {
        List<string> calls = [];
        UpdateApiKeyCommand command = ActiveStatusUpdateCommand();
        ApiKeyControlPlaneSnapshot revoked = Snapshot(
            command.UserId,
            command.ApiKeyId,
            status: ApiKeyPersistentStatus.Revoked,
            effectiveStatus: ApiKeyEffectiveStatus.Revoked);
        ApiKeyUpdatedOutcome expected = UpdatedOutcome(revoked, isReplay: false);
        FakeOwner owner = new(calls)
        {
            UpdateResult = Result.Success(expected),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, revoked),
            SubscriptionThatMustNotRun(calls),
            owner);

        Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Value);
        Assert.Equal(
            ["preflight:update", "read", "owner:update"],
            calls);
        Assert.Null(owner.LastUpdateDecision);
    }

    [Fact]
    public async Task RevokedRotateSkipsSubscriptionAndLetsIdentityOwnerDecide()
    {
        List<string> calls = [];
        RotateApiKeyCommand command = RotateCommand();
        ApiKeyControlPlaneSnapshot revoked = Snapshot(
            command.UserId,
            command.ApiKeyId,
            status: ApiKeyPersistentStatus.Revoked,
            effectiveStatus: ApiKeyEffectiveStatus.Revoked);
        ApiKeyCreatedOutcome expected = CreatedOutcome(
            command.UserId,
            EntityId.New(),
            isReplay: false);
        FakeOwner owner = new(calls)
        {
            RotateResult = Result.Success(expected),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, revoked),
            SubscriptionThatMustNotRun(calls),
            owner);

        Result<ApiKeyCreatedOutcome> result = await orchestrator.RotateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Value);
        Assert.Equal(
            ["preflight:rotate", "read", "owner:rotate"],
            calls);
        Assert.Null(owner.LastRotateDecision);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("disable")]
    public async Task MetadataOnlyAndDisableUpdatesDoNotRequireSubscription(
        string mutation)
    {
        List<string> calls = [];
        UpdateApiKeyCommand command = string.Equals(
            mutation,
            "disable",
            StringComparison.Ordinal)
            ? DisableCommand()
            : MetadataUpdateCommand();
        ApiKeyControlPlaneSnapshot active = Snapshot(
            command.UserId,
            command.ApiKeyId);
        ApiKeyUpdatedOutcome expected = UpdatedOutcome(active, isReplay: false);
        FakeOwner owner = new(calls)
        {
            UpdateResult = Result.Success(expected),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, active),
            SubscriptionThatMustNotRun(calls),
            owner);

        Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["preflight:update", "read", "owner:update"],
            calls);
        Assert.Null(owner.LastUpdateDecision);
    }

    [Fact]
    public async Task RevokeNeverRequiresSubscription()
    {
        List<string> calls = [];
        RevokeApiKeyCommand command = RevokeCommand();
        ApiKeyControlPlaneSnapshot active = Snapshot(
            command.UserId,
            command.ApiKeyId);
        ApiKeyRevokedOutcome expected = new(204, IsReplay: false, "\"v8\"");
        FakeOwner owner = new(calls)
        {
            RevokeResult = Result.Success(expected),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, active),
            SubscriptionThatMustNotRun(calls),
            owner);

        Result<ApiKeyRevokedOutcome> result = await orchestrator.RevokeAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Same(expected, result.Value);
        Assert.Equal(
            ["preflight:revoke", "read", "owner:revoke"],
            calls);
    }

    [Theory]
    [InlineData("disabled_to_active")]
    [InlineData("expired_to_active")]
    public async Task ReactivationReadsSubscriptionForTargetOwnerAndBoundGroup(
        string transition)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId targetUserId = EntityId.New();
        EntityId apiKeyId = EntityId.New();
        EntityId groupId = EntityId.New();
        UpdateApiKeyCommand command = string.Equals(
            transition,
            "disabled_to_active",
            StringComparison.Ordinal)
            ? ActiveStatusUpdateCommand(actorId, targetUserId, apiKeyId)
            : FutureExpiryUpdateCommand(actorId, targetUserId, apiKeyId);
        ApiKeyControlPlaneSnapshot current = string.Equals(
            transition,
            "disabled_to_active",
            StringComparison.Ordinal)
            ? Snapshot(
                targetUserId,
                apiKeyId,
                groupId,
                ApiKeyPersistentStatus.Disabled,
                ApiKeyEffectiveStatus.Disabled)
            : Snapshot(
                targetUserId,
                apiKeyId,
                groupId,
                ApiKeyPersistentStatus.Active,
                ApiKeyEffectiveStatus.Expired,
                ObservedAt.AddHours(-1));
        FakeSubscriptionReader subscription = SuccessfulSubscription(
            calls,
            targetUserId,
            groupId);
        FakeOwner owner = new(calls)
        {
            UpdateResult = Result.Success(
                UpdatedOutcome(current, isReplay: false)),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, current),
            subscription,
            owner);

        Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["preflight:update", "read", "subscription", "owner:update"],
            calls);
        Assert.Equal(targetUserId, subscription.LastUserId);
        Assert.Equal(groupId, subscription.LastGroupId);
        AssertAuthorizedDecision(
            owner.LastUpdateDecision,
            targetUserId,
            groupId);
    }

    [Fact]
    public async Task RotateReadsSubscriptionForTargetOwnerAndBoundGroup()
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId targetUserId = EntityId.New();
        EntityId apiKeyId = EntityId.New();
        EntityId groupId = EntityId.New();
        RotateApiKeyCommand command = RotateCommand(
            actorId,
            targetUserId,
            apiKeyId);
        ApiKeyControlPlaneSnapshot active = Snapshot(
            targetUserId,
            apiKeyId,
            groupId);
        FakeSubscriptionReader subscription = SuccessfulSubscription(
            calls,
            targetUserId,
            groupId);
        FakeOwner owner = new(calls)
        {
            RotateResult = Result.Success(
                CreatedOutcome(targetUserId, EntityId.New(), isReplay: false)),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, active),
            subscription,
            owner);

        Result<ApiKeyCreatedOutcome> result = await orchestrator.RotateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["preflight:rotate", "read", "subscription", "owner:rotate"],
            calls);
        Assert.Equal(targetUserId, subscription.LastUserId);
        Assert.Equal(groupId, subscription.LastGroupId);
        AssertAuthorizedDecision(
            owner.LastRotateDecision,
            targetUserId,
            groupId);
    }

    [Theory]
    [InlineData("update", "subscription_required",
        ApiKeyAccessDecisionKind.SubscriptionRequired)]
    [InlineData("update", "subscription_inactive",
        ApiKeyAccessDecisionKind.SubscriptionInactive)]
    [InlineData("rotate", "subscription_required",
        ApiKeyAccessDecisionKind.SubscriptionRequired)]
    [InlineData("rotate", "subscription_inactive",
        ApiKeyAccessDecisionKind.SubscriptionInactive)]
#pragma warning disable MA0051 // Scenario keeps the complete cross-owner evidence flow visible.
    public async Task StableSubscriptionDenialIsPassedToIdentityOwner(
        string operation,
        string errorCode,
        ApiKeyAccessDecisionKind expectedKind)
    {
        List<string> calls = [];
        EntityId actorId = EntityId.New();
        EntityId targetUserId = EntityId.New();
        EntityId apiKeyId = EntityId.New();
        EntityId groupId = EntityId.New();
        ApiKeyControlPlaneSnapshot current = string.Equals(
            operation,
            "update",
            StringComparison.Ordinal)
            ? Snapshot(
                targetUserId,
                apiKeyId,
                groupId,
                ApiKeyPersistentStatus.Disabled,
                ApiKeyEffectiveStatus.Disabled)
            : Snapshot(targetUserId, apiKeyId, groupId);
        FakeOwner owner = new(calls)
        {
            UpdateResult = Failure<ApiKeyUpdatedOutcome>(
                errorCode,
                "Identity stored the denial."),
            RotateResult = Failure<ApiKeyCreatedOutcome>(
                errorCode,
                "Identity stored the denial."),
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            ProceedingPreflight(calls),
            SuccessfulReader(calls, current),
            new FakeSubscriptionReader(
                calls,
                Failure<SubscriptionAccessSnapshot>(
                    errorCode,
                    "Subscription denied access.")),
            owner);

        ResultError error;
        ApiKeyAccessDecision? decision;
        if (string.Equals(operation, "update", StringComparison.Ordinal))
        {
            Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
                ActiveStatusUpdateCommand(actorId, targetUserId, apiKeyId),
                TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure);
            error = result.Error;
            decision = owner.LastUpdateDecision;
        }
        else
        {
            Result<ApiKeyCreatedOutcome> result = await orchestrator.RotateAsync(
                RotateCommand(actorId, targetUserId, apiKeyId),
                TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure);
            error = result.Error;
            decision = owner.LastRotateDecision;
        }

        Assert.Equal(errorCode, error.Code);
        Assert.Equal(targetUserId, decision?.UserId);
        Assert.Equal(groupId, decision?.GroupId);
        Assert.Equal(expectedKind, decision?.Kind);
        Assert.Null(decision?.SubscriptionId);
        Assert.Null(decision?.ObservedAt);
        Assert.Equal(
            [
                $"preflight:{operation}",
                "read",
                "subscription",
                $"owner:{operation}",
            ],
            calls);
    }
#pragma warning restore MA0051

    [Theory]
    [InlineData("preflight")]
    [InlineData("read")]
    [InlineData("subscription")]
    [InlineData("owner")]
#pragma warning disable MA0051 // Scenario verifies metadata at every orchestration boundary.
    public async Task UpdateFailureIsForwardedWithoutLosingErrorMetadata(
        string failingStage)
    {
        const string errorCode = "dependency_unavailable";
        List<string> calls = [];
        UpdateApiKeyCommand command = ActiveStatusUpdateCommand();
        ApiKeyControlPlaneSnapshot disabled = Snapshot(
            command.UserId,
            command.ApiKeyId,
            status: ApiKeyPersistentStatus.Disabled,
            effectiveStatus: ApiKeyEffectiveStatus.Disabled);
        ResultErrorPresentation presentation = new(
            errorCode,
            503,
            "Dependency unavailable",
            "The staged dependency failed.",
            Retryable: true,
            RetryAfterSeconds: 7);
        Result<ApiKeyUpdatedOutcome?> preflightResult =
            Result.Success<ApiKeyUpdatedOutcome?>(null);
        Result<ApiKeyControlPlaneSnapshot> readResult =
            Result.Success(disabled);
        Result<SubscriptionAccessSnapshot> subscriptionResult =
            Result.Success(Subscription(
                command.UserId,
                disabled.GroupId));
        Result<ApiKeyUpdatedOutcome> ownerResult = Result.Success(
            UpdatedOutcome(disabled, isReplay: false));

        switch (failingStage)
        {
            case "preflight":
                preflightResult = Failure<ApiKeyUpdatedOutcome?>(
                    errorCode,
                    "Preflight failed.",
                    presentation);
                break;
            case "read":
                readResult = Failure<ApiKeyControlPlaneSnapshot>(
                    errorCode,
                    "Read failed.",
                    presentation);
                break;
            case "subscription":
                subscriptionResult = Failure<SubscriptionAccessSnapshot>(
                    errorCode,
                    "Subscription failed.",
                    presentation);
                break;
            case "owner":
                ownerResult = Failure<ApiKeyUpdatedOutcome>(
                    errorCode,
                    "Owner failed.",
                    presentation);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown test stage '{failingStage}'.");
        }

        FakePreflight preflight = new(calls)
        {
            UpdateResult = preflightResult,
        };
        FakeReader reader = new(calls)
        {
            GetResult = readResult,
        };
        FakeSubscriptionReader subscription = new(calls, subscriptionResult);
        FakeOwner owner = new(calls)
        {
            UpdateResult = ownerResult,
        };
        ApiKeyMutationOrchestrator orchestrator = Orchestrator(
            preflight,
            reader,
            subscription,
            owner);

        Result<ApiKeyUpdatedOutcome> result = await orchestrator.UpdateAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(errorCode, result.Error.Code);
        Assert.Equal(7, result.Error.RetryAfterSeconds);
        Assert.Equal("\"v41\"", result.Error.ETag);
        Assert.Same(presentation, result.Error.Presentation);
        string[] expectedCalls = failingStage switch
        {
            "preflight" => ["preflight:update"],
            "read" => ["preflight:update", "read"],
            "subscription" =>
                ["preflight:update", "read", "subscription"],
            _ =>
                ["preflight:update", "read", "subscription", "owner:update"],
        };
        Assert.Equal(expectedCalls, calls);
    }
#pragma warning restore MA0051

    private static ApiKeyMutationOrchestrator Orchestrator(
        IApiKeyMutationIdempotencyPreflight preflight,
        IApiKeyControlPlaneReader reader,
        ISubscriptionAccessReader subscription,
        IApiKeyMutationOwner owner) => new(
            preflight,
            reader,
            subscription,
            owner);

    private static FakePreflight ProceedingPreflight(
        ICollection<string> calls) => new(calls);

    private static FakeReader SuccessfulReader(
        ICollection<string> calls,
        ApiKeyControlPlaneSnapshot snapshot) => new(calls)
        {
            GetResult = Result.Success(snapshot),
        };

    private static FakeReader ReaderThatMustNotRun(
        ICollection<string> calls) => new(calls)
        {
            GetResult = Failure<ApiKeyControlPlaneSnapshot>(
                "unexpected",
                "Resource read must not run."),
        };

    private static FakeSubscriptionReader SuccessfulSubscription(
        ICollection<string> calls,
        EntityId userId,
        EntityId groupId) => new(
            calls,
            Result.Success(Subscription(userId, groupId)));

    private static FakeSubscriptionReader SubscriptionThatMustNotRun(
        ICollection<string> calls) => new(
            calls,
            Failure<SubscriptionAccessSnapshot>(
                "unexpected",
                "Subscription read must not run."));

    private static FakeOwner OwnerThatMustNotRun(
        ICollection<string> calls) => new(calls);

    private static UpdateApiKeyCommand MetadataUpdateCommand()
    {
        EntityId userId = EntityId.New();
        return new UpdateApiKeyCommand(
            EntityId.New(),
            new ApiKeyActor(userId, SystemRole.User, 4),
            ApiKeyAccessMode.Self,
            userId,
            EntityId.New(),
            "m1-e5-patch-metadata",
            7,
            SetName: true,
            Name: "Renamed CLI",
            SetStatus: false,
            Status: null,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: null,
            IpAddress: "192.0.2.10",
            UserAgent: "PoolAI.Test");
    }

    private static UpdateApiKeyCommand DisableCommand()
    {
        UpdateApiKeyCommand command = MetadataUpdateCommand();
        return command with
        {
            IdempotencyKey = "m1-e5-disable",
            SetName = false,
            Name = null,
            SetStatus = true,
            Status = ApiKeyPersistentStatus.Disabled,
        };
    }

    private static UpdateApiKeyCommand ActiveStatusUpdateCommand()
    {
        EntityId userId = EntityId.New();
        return ActiveStatusUpdateCommand(userId, userId, EntityId.New());
    }

    private static UpdateApiKeyCommand ActiveStatusUpdateCommand(
        EntityId actorId,
        EntityId targetUserId,
        EntityId apiKeyId) => new(
            EntityId.New(),
            new ApiKeyActor(actorId, SystemRole.Admin, 9),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId,
            "m1-e5-enable",
            7,
            SetName: false,
            Name: null,
            SetStatus: true,
            Status: ApiKeyPersistentStatus.Active,
            SetExpiresAt: false,
            ExpiresAt: null,
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: "Re-enable approved key.",
            IpAddress: "192.0.2.11",
            UserAgent: "PoolAI.Test");

    private static UpdateApiKeyCommand FutureExpiryUpdateCommand(
        EntityId actorId,
        EntityId targetUserId,
        EntityId apiKeyId) => new(
            EntityId.New(),
            new ApiKeyActor(actorId, SystemRole.Admin, 9),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId,
            "m1-e5-extend-expiry",
            7,
            SetName: false,
            Name: null,
            SetStatus: false,
            Status: null,
            SetExpiresAt: true,
            ExpiresAt: ObservedAt.AddDays(30),
            SetAllowedCidrs: false,
            AllowedCidrs: null,
            Reason: "Restore active subscription-gated key.",
            IpAddress: "192.0.2.11",
            UserAgent: "PoolAI.Test");

    private static RevokeApiKeyCommand RevokeCommand()
    {
        EntityId userId = EntityId.New();
        return new RevokeApiKeyCommand(
            EntityId.New(),
            new ApiKeyActor(userId, SystemRole.User, 4),
            ApiKeyAccessMode.Self,
            userId,
            EntityId.New(),
            "m1-e5-revoke",
            7,
            "No longer needed.",
            "192.0.2.10",
            "PoolAI.Test");
    }

    private static RotateApiKeyCommand RotateCommand()
    {
        EntityId userId = EntityId.New();
        return RotateCommand(userId, userId, EntityId.New());
    }

    private static RotateApiKeyCommand RotateCommand(
        EntityId actorId,
        EntityId targetUserId,
        EntityId apiKeyId) => new(
            EntityId.New(),
            new ApiKeyActor(actorId, SystemRole.Admin, 9),
            ApiKeyAccessMode.AdminProxy,
            targetUserId,
            apiKeyId,
            "m1-e5-rotate",
            7,
            "Rotate the credential.",
            "192.0.2.11",
            "PoolAI.Test");

    private static ApiKeyControlPlaneSnapshot Snapshot(
        EntityId userId,
        EntityId apiKeyId,
        EntityId? groupId = null,
        ApiKeyPersistentStatus status = ApiKeyPersistentStatus.Active,
        ApiKeyEffectiveStatus effectiveStatus = ApiKeyEffectiveStatus.Active,
        DateTimeOffset? expiresAt = null) => new(
            apiKeyId,
            userId,
            groupId ?? EntityId.New(),
            "CLI",
            "sk-pool-AbCdEf12",
            status,
            effectiveStatus,
            expiresAt,
            ["10.0.0.0/24"],
            LastUsedAt: null,
            Version: 7,
            CreatedAt: ObservedAt.AddDays(-10),
            UpdatedAt: ObservedAt.AddDays(-1),
            ObservedAt);

    private static ApiKeyUpdatedOutcome UpdatedOutcome(
        ApiKeyControlPlaneSnapshot snapshot,
        bool isReplay) => new(
            200,
            isReplay,
            snapshot,
            $"\"v{snapshot.Version}\"");

    private static ApiKeyCreatedOutcome CreatedOutcome(
        EntityId userId,
        EntityId apiKeyId,
        bool isReplay) => new(
            201,
            isReplay,
            Snapshot(userId, apiKeyId) with { Version = 1 },
            "sk-pool-AbCdEf12abcdefghijklmnopqrstuvwxyz012345678",
            "\"v1\"",
            $"/api/v1/me/api-keys/{apiKeyId.Value:D}");

    private static SubscriptionAccessSnapshot Subscription(
        EntityId userId,
        EntityId groupId) => new(
            EntityId.New(),
            userId,
            groupId,
            "standard",
            ObservedAt.AddDays(-1),
            ObservedAt.AddDays(30),
            SubscriptionEffectiveStatus.Active,
            4,
            ObservedAt);

    private static void AssertAuthorizedDecision(
        ApiKeyAccessDecision? decision,
        EntityId expectedUserId,
        EntityId expectedGroupId)
    {
        ApiKeyAccessDecision authorized =
            Assert.IsType<ApiKeyAccessDecision>(decision);
        Assert.Equal(ApiKeyAccessDecisionKind.Authorized, authorized.Kind);
        Assert.Equal(expectedUserId, authorized.UserId);
        Assert.Equal(expectedGroupId, authorized.GroupId);
        Assert.NotNull(authorized.SubscriptionId);
        Assert.Equal(ObservedAt, authorized.ObservedAt);
    }

    private static Result<T> Failure<T>(
        string code,
        string description,
        ResultErrorPresentation? presentation = null) =>
        Result.Failure<T>(
            code,
            description,
            retryAfterSeconds: presentation?.RetryAfterSeconds,
            etag: presentation is null ? null : "\"v41\"",
            presentation);

    private sealed class FakePreflight(
        ICollection<string> calls) : IApiKeyMutationIdempotencyPreflight
    {
        public Result<ApiKeyUpdatedOutcome?> UpdateResult { get; init; } =
            Result.Success<ApiKeyUpdatedOutcome?>(null);

        public Result<ApiKeyRevokedOutcome?> RevokeResult { get; init; } =
            Result.Success<ApiKeyRevokedOutcome?>(null);

        public Result<ApiKeyCreatedOutcome?> RotateResult { get; init; } =
            Result.Success<ApiKeyCreatedOutcome?>(null);

        public ValueTask<Result<ApiKeyUpdatedOutcome?>> TryReplayUpdateAsync(
            UpdateApiKeyCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("preflight:update");
            return ValueTask.FromResult(UpdateResult);
        }

        public ValueTask<Result<ApiKeyRevokedOutcome?>> TryReplayRevokeAsync(
            RevokeApiKeyCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("preflight:revoke");
            return ValueTask.FromResult(RevokeResult);
        }

        public ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayRotateAsync(
            RotateApiKeyCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("preflight:rotate");
            return ValueTask.FromResult(RotateResult);
        }
    }

    private sealed class FakeReader(
        ICollection<string> calls) : IApiKeyControlPlaneReader
    {
        public Result<ApiKeyControlPlaneSnapshot> GetResult { get; init; } =
            Failure<ApiKeyControlPlaneSnapshot>(
                "unexpected",
                "Get result was not configured.");

        public ValueTask<Result<ApiKeyPage>> ListAsync(
            ListApiKeysQuery query,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Failure<ApiKeyPage>(
                "unexpected",
                "List must not run."));

        public ValueTask<Result<ApiKeyControlPlaneSnapshot>> GetAsync(
            GetApiKeyQuery query,
            CancellationToken cancellationToken)
        {
            calls.Add("read");
            return ValueTask.FromResult(GetResult);
        }
    }

    private sealed class FakeSubscriptionReader(
        ICollection<string> calls,
        Result<SubscriptionAccessSnapshot> result) : ISubscriptionAccessReader
    {
        public EntityId? LastUserId { get; private set; }

        public EntityId? LastGroupId { get; private set; }

        public ValueTask<Result<SubscriptionAccessSnapshot>>
            GetEffectiveAccessAsync(
                EntityId userId,
                EntityId groupId,
                CancellationToken cancellationToken)
        {
            calls.Add("subscription");
            LastUserId = userId;
            LastGroupId = groupId;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeOwner(
        ICollection<string> calls) : IApiKeyMutationOwner
    {
        public Result<ApiKeyUpdatedOutcome> UpdateResult { get; init; } =
            Failure<ApiKeyUpdatedOutcome>(
                "unexpected",
                "Update owner must not run.");

        public Result<ApiKeyRevokedOutcome> RevokeResult { get; init; } =
            Failure<ApiKeyRevokedOutcome>(
                "unexpected",
                "Revoke owner must not run.");

        public Result<ApiKeyCreatedOutcome> RotateResult { get; init; } =
            Failure<ApiKeyCreatedOutcome>(
                "unexpected",
                "Rotate owner must not run.");

        public ApiKeyAccessDecision? LastUpdateDecision { get; private set; }

        public ApiKeyAccessDecision? LastRotateDecision { get; private set; }

        public ValueTask<Result<ApiKeyUpdatedOutcome>> UpdateAsync(
            UpdateApiKeyCommand command,
            ApiKeyControlPlaneSnapshot snapshot,
            ApiKeyAccessDecision? accessDecision,
            CancellationToken cancellationToken)
        {
            calls.Add("owner:update");
            LastUpdateDecision = accessDecision;
            return ValueTask.FromResult(UpdateResult);
        }

        public ValueTask<Result<ApiKeyRevokedOutcome>> RevokeAsync(
            RevokeApiKeyCommand command,
            ApiKeyControlPlaneSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            calls.Add("owner:revoke");
            return ValueTask.FromResult(RevokeResult);
        }

        public ValueTask<Result<ApiKeyCreatedOutcome>> RotateAsync(
            RotateApiKeyCommand command,
            ApiKeyControlPlaneSnapshot snapshot,
            ApiKeyAccessDecision? accessDecision,
            CancellationToken cancellationToken)
        {
            calls.Add("owner:rotate");
            LastRotateDecision = accessDecision;
            return ValueTask.FromResult(RotateResult);
        }
    }
}
