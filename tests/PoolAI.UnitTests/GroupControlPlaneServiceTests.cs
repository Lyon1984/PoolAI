#pragma warning disable MA0051 // Each test keeps one complete command protocol visible.
using System.Buffers.Binary;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class GroupControlPlaneServiceTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        17,
        8,
        0,
        0,
        TimeSpan.Zero);

    private static readonly GroupActor Admin = new(
        EntityId.New(),
        GroupControlRole.Admin,
        TokenVersion: 7);

    [Fact]
    public void GroupInputsPolicyAndSnapshotEnforceTheirBoundaries()
    {
        Assert.Equal("Research", GroupInput.Name("  Research  "));
        Assert.Equal("description", GroupInput.Description("description"));
        Assert.Null(GroupInput.Description(null));
        Assert.Equal("visible-key", GroupInput.IdempotencyKey("visible-key"));
        Assert.Equal("archive", GroupInput.Reason("  archive  "));

        Assert.Throws<ArgumentNullException>(() => GroupInput.Name(null!));
        Assert.Throws<ArgumentException>(() => GroupInput.Name("\n"));
        Assert.Throws<ArgumentException>(() => GroupInput.Name(new string('n', 101)));
        Assert.Throws<ArgumentException>(() => GroupInput.Description(new string('d', 1001)));
        Assert.Throws<ArgumentNullException>(() => GroupInput.IdempotencyKey(null!));
        Assert.Throws<ArgumentException>(() => GroupInput.IdempotencyKey(string.Empty));
        Assert.Throws<ArgumentException>(() => GroupInput.IdempotencyKey("contains space"));
        Assert.Throws<ArgumentException>(() => GroupInput.IdempotencyKey(new string('k', 129)));
        Assert.Throws<ArgumentNullException>(() => GroupInput.Reason(null!));
        Assert.Throws<ArgumentException>(() => GroupInput.Reason("\r\n"));
        Assert.Throws<ArgumentException>(() => GroupInput.Reason(new string('r', 501)));
        Assert.Throws<ArgumentNullException>(() => new GroupQuotaPolicy(null!));
        Assert.Throws<ArgumentException>(() => new GroupQuotaPolicy(new byte[31]));

        byte[] pepper = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        GroupQuotaPolicy policy = new(pepper);
        pepper[0] = 255;
        Assert.Equal(0, policy.RequestHashPepper[0]);

        GroupResource resource = Group(version: 4, lifecycle: GroupLifecycle.Active);
        GroupResourceSnapshot snapshot = resource.ToSnapshot();
        Assert.Equal(resource.Id, snapshot.GroupId);
        Assert.Equal(resource.Name, snapshot.Name);
        Assert.Equal(resource.Lifecycle, snapshot.Lifecycle);
        Assert.Equal(resource.Version, snapshot.Version);
    }

    [Fact]
    public async Task ListGetAndStatusReaderCoverPolicyCursorAndMissingResources()
    {
        GroupResource first = Group(version: 2, name: "First");
        GroupResource second = Group(version: 3, name: "Second") with
        {
            CreatedAt = Now.AddMinutes(-1),
        };
        TestEnvironment environment = new();
        environment.Repository.ListResult = new GroupSlice([first, second], HasMore: true);

        Result<GroupPage> denied = await environment.Service.ExecuteAsync(
            new ListGroupsQuery(
                new GroupActor(Admin.UserId, GroupControlRole.User, Admin.TokenVersion),
                Cursor: null),
            TestContext.Current.CancellationToken);
        AssertFailure(denied, GroupErrorCodes.RoleRequired);

        Result<GroupPage> staleActor = await environment.Service.ExecuteAsync(
            new ListGroupsQuery(Admin with { TokenVersion = 0 }, Cursor: null),
            TestContext.Current.CancellationToken);
        AssertFailure(staleActor, GroupErrorCodes.RoleRequired);

        foreach ((string? cursor, int limit) in new (string?, int)[]
                 {
                     (null, 0),
                     (null, 101),
                     ("invalid", 50),
                     (new string('A', 34), 50),
                     (new string('a', 34), 50),
                     (new string('0', 34), 50),
                     (new string('A', 33) + "-", 50),
                     (new string('A', 33) + "_", 50),
                 })
        {
            Result<GroupPage> invalid = await environment.Service.ExecuteAsync(
                new ListGroupsQuery(Admin, cursor, limit),
                TestContext.Current.CancellationToken);
            AssertFailure(invalid, GroupErrorCodes.InvalidRequest);
        }

        Result<GroupPage> listed = await environment.Service.ExecuteAsync(
            new ListGroupsQuery(
                new GroupActor(Admin.UserId, GroupControlRole.Auditor, Admin.TokenVersion),
                Cursor: null,
                Limit: 2),
            TestContext.Current.CancellationToken);
        Assert.True(listed.IsSuccess);
        Assert.Equal(2, listed.Value.Data.Count);
        Assert.True(listed.Value.HasMore);
        Assert.NotNull(listed.Value.NextCursor);

        environment.Repository.ListResult = new GroupSlice([], HasMore: false);
        Result<GroupPage> next = await environment.Service.ExecuteAsync(
            new ListGroupsQuery(Admin, listed.Value.NextCursor, Limit: 2),
            TestContext.Current.CancellationToken);
        Assert.True(next.IsSuccess);
        Assert.NotNull(environment.Repository.LastCursor);
        Assert.Equal(second.Id, environment.Repository.LastCursor!.Id);
        Assert.Equal(second.CreatedAt, environment.Repository.LastCursor.CreatedAt);

        environment.Repository.GetResult = first;
        Result<GroupView> found = await environment.Service.ExecuteAsync(
            new GetGroupQuery(
                new GroupActor(Admin.UserId, GroupControlRole.Operator, Admin.TokenVersion),
                first.Id),
            TestContext.Current.CancellationToken);
        Assert.True(found.IsSuccess);
        Assert.Equal(first.Name, found.Value.Name);

        Result<GroupSnapshot> snapshot = await environment.Service.GetAsync(
            first.Id,
            TestContext.Current.CancellationToken);
        Assert.True(snapshot.IsSuccess);
        Assert.Equal(first.HasCurrentQuotaPeriod, snapshot.Value.HasCurrentQuotaPeriod);

        environment.Repository.GetResult = null;
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetGroupQuery(Admin, first.Id),
                TestContext.Current.CancellationToken),
            GroupErrorCodes.ResourceNotFound);
        AssertFailure(
            await environment.Service.GetAsync(first.Id, TestContext.Current.CancellationToken),
            GroupErrorCodes.ResourceNotFound);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetGroupQuery(
                    new GroupActor(Admin.UserId, GroupControlRole.User, Admin.TokenVersion),
                    first.Id),
                TestContext.Current.CancellationToken),
            GroupErrorCodes.RoleRequired);
    }

    [Fact]
    public async Task CreateCoversValidationConflictSuccessAndBothReplayKinds()
    {
        CreateGroupCommand valid = CreateCommand();
        TestEnvironment policy = new();
        AssertFailure(
            await policy.Service.ExecuteAsync(
                valid with
                {
                    Actor = new GroupActor(Admin.UserId, GroupControlRole.Operator, 1),
                },
                TestContext.Current.CancellationToken),
            GroupErrorCodes.RoleRequired);

        foreach (CreateGroupCommand invalid in new[]
                 {
                     valid with { IdempotencyKey = "bad key" },
                     valid with { Name = "\n" },
                     valid with { Description = new string('d', 1001) },
                     valid with { TotalTokens = 0 },
                     valid with { TotalTokens = 9_007_199_254_740_992 },
                 })
        {
            TestEnvironment environment = new();
            AssertFailure(
                await environment.Service.ExecuteAsync(
                    invalid,
                    TestContext.Current.CancellationToken),
                GroupErrorCodes.ValidationFailed);
            Assert.Equal(0, environment.UnitOfWork.BeginCalls);
        }

        TestEnvironment conflict = new();
        conflict.Repository.CreateFactory = static _ => new GroupWriteResult(
            GroupWriteDisposition.NameConflict,
            Group: null);
        Result<GroupCommandOutcome> firstConflict = await conflict.Service.ExecuteAsync(
            valid,
            TestContext.Current.CancellationToken);
        AssertFailure(firstConflict, GroupErrorCodes.ResourceConflict);
        Assert.Equal(1, conflict.UnitOfWork.CommitCalls);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, Assert.Single(
            conflict.Idempotency.Completions).TerminalStatus);

        conflict.Idempotency.ReplayCompletedRequests = true;
        Result<GroupCommandOutcome> replayedConflict = await conflict.Service.ExecuteAsync(
            valid,
            TestContext.Current.CancellationToken);
        AssertFailure(replayedConflict, GroupErrorCodes.ResourceConflict);
        Assert.Equal(1, conflict.Repository.CreateCalls);

        TestEnvironment success = new();
        success.Repository.CreateFactory = write => new GroupWriteResult(
            GroupWriteDisposition.Written,
            Group(
                write.GroupId,
                version: 1,
                name: write.Name,
                description: write.Description));
        Result<GroupCommandOutcome> created = await success.Service.ExecuteAsync(
            valid,
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);
        Assert.Equal(201, created.Value.StatusCode);
        Assert.False(created.Value.IsReplay);
        Assert.Equal("\"v1\"", created.Value.ETag);
        Assert.EndsWith(created.Value.Value.Id.Value.ToString("D"), created.Value.Location, StringComparison.Ordinal);
        Assert.Equal("groupquota.group.created", Assert.Single(success.Audit.Entries).Action);
        Assert.Equal("group_created", Assert.Single(success.Outbox.Events).EventType);
        Assert.Equal(1, success.UnitOfWork.CommitCalls);
        Assert.StartsWith(
            "group-create:",
            success.Repository.LastCreate!.QuotaIdempotencyKey,
            StringComparison.Ordinal);

        success.Idempotency.ReplayCompletedRequests = true;
        Result<GroupCommandOutcome> replay = await success.Service.ExecuteAsync(
            valid,
            TestContext.Current.CancellationToken);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(created.Value.Value, replay.Value.Value);
        Assert.Equal(created.Value.Location, replay.Value.Location);
        Assert.Equal(1, success.Repository.CreateCalls);

        Result<GroupCommandOutcome> normalizedReplay = await success.Service.ExecuteAsync(
            valid with { Name = "  Research  " },
            TestContext.Current.CancellationToken);
        Assert.True(normalizedReplay.IsSuccess);
        Assert.True(normalizedReplay.Value.IsReplay);

        Result<GroupCommandOutcome> changedRequest = await success.Service.ExecuteAsync(
            valid with { Name = "Different research" },
            TestContext.Current.CancellationToken);
        AssertFailure(changedRequest, GroupErrorCodes.IdempotencyConflict);
        Assert.Equal(1, success.Repository.CreateCalls);

        foreach (CommandIdempotencyAcquireResult acquire in new[]
                 {
                     CommandIdempotencyAcquireResult.Conflict,
                     CommandIdempotencyAcquireResult.Busy,
                 })
        {
            TestEnvironment coordination = new();
            coordination.Idempotency.NextAcquire = acquire;
            Result<GroupCommandOutcome> result = await coordination.Service.ExecuteAsync(
                valid,
                TestContext.Current.CancellationToken);
            AssertFailure(
                result,
                acquire.Disposition == CommandIdempotencyDisposition.Conflict
                    ? GroupErrorCodes.IdempotencyConflict
                    : GroupErrorCodes.CoordinationUnavailable);
            Assert.Equal(0, coordination.Repository.CreateCalls);
        }
    }

    [Fact]
    public async Task UpdateCoversPolicyValidationDispositionsNoOpSuccessAndReplay()
    {
        EntityId groupId = EntityId.New();
        UpdateGroupCommand valid = UpdateCommand(groupId);
        TestEnvironment policy = new();
        AssertFailure(
            await policy.Service.ExecuteAsync(
                valid with
                {
                    Actor = new GroupActor(Admin.UserId, GroupControlRole.Operator, 1),
                },
                TestContext.Current.CancellationToken),
            GroupErrorCodes.RoleRequired);
        AssertFailure(
            await policy.Service.ExecuteAsync(
                valid with
                {
                    HasStatus = true,
                    Status = GroupLifecycle.Active,
                    Reason = "activate",
                },
                TestContext.Current.CancellationToken),
            GroupErrorCodes.GroupActivationNotReady);

        foreach (UpdateGroupCommand invalid in new[]
                 {
                     valid with { IdempotencyKey = "bad key" },
                     valid with { ExpectedVersion = 0 },
                     valid with { HasName = false },
                     valid with { Name = null },
                     valid with { Name = "\n" },
                     valid with { Reason = "\n" },
                 })
        {
            TestEnvironment environment = new();
            AssertFailure(
                await environment.Service.ExecuteAsync(
                    invalid,
                    TestContext.Current.CancellationToken),
                GroupErrorCodes.ValidationFailed);
        }

        foreach ((GroupWriteResult write, string code, int status) in new[]
                 {
                     (new GroupWriteResult(GroupWriteDisposition.NotFound, null), GroupErrorCodes.ResourceNotFound, 404),
                     (new GroupWriteResult(GroupWriteDisposition.VersionConflict, null, CurrentVersion: 9), GroupErrorCodes.VersionConflict, 412),
                     (new GroupWriteResult(GroupWriteDisposition.NameConflict, null), GroupErrorCodes.ResourceConflict, 409),
                     (new GroupWriteResult(GroupWriteDisposition.ArchiveBlocked, null), GroupErrorCodes.ResourceConflict, 409),
                     (new GroupWriteResult(GroupWriteDisposition.LifecycleConflict, null), GroupErrorCodes.ResourceConflict, 409),
                 })
        {
            TestEnvironment environment = new();
            environment.Repository.UpdateResults.Enqueue(write);
            Result<GroupCommandOutcome> result = await environment.Service.ExecuteAsync(
                valid,
                TestContext.Current.CancellationToken);
            AssertFailure(result, code);
            Assert.Equal(status, result.Error.Presentation!.Status);
            Assert.Equal(status == 412 ? "\"v9\"" : null, result.Error.ETag);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        }

        GroupResource before = Group(groupId, version: 4, name: "Before", description: "old");
        GroupResource after = before with
        {
            Name = "After",
            Description = "new",
            Lifecycle = GroupLifecycle.Archived,
            Version = 5,
            UpdatedAt = Now.AddMinutes(1),
        };
        TestEnvironment changed = new();
        changed.Repository.UpdateResults.Enqueue(new GroupWriteResult(
            GroupWriteDisposition.Written,
            after,
            before,
            WasChanged: true,
            CurrentVersion: 5));
        UpdateGroupCommand change = valid with
        {
            ExpectedVersion = 4,
            Name = "After",
            HasDescription = true,
            Description = "new",
            HasStatus = true,
            Status = GroupLifecycle.Archived,
            Reason = "archive",
        };
        Result<GroupCommandOutcome> updated = await changed.Service.ExecuteAsync(
            change,
            TestContext.Current.CancellationToken);
        Assert.True(updated.IsSuccess);
        Assert.Equal("\"v5\"", updated.Value.ETag);
        Assert.Equal("groupquota.group.updated", Assert.Single(changed.Audit.Entries).Action);
        Assert.Equal("group_updated", Assert.Single(changed.Outbox.Events).EventType);
        Assert.Equal(1, changed.UnitOfWork.CommitCalls);
        Assert.Equal(groupId, changed.Repository.LastUpdate!.GroupId);
        Assert.Equal(4, changed.Repository.LastUpdate.ExpectedVersion);
        Assert.True(changed.Repository.LastUpdate.HasName);
        Assert.Equal("After", changed.Repository.LastUpdate.Name);
        Assert.True(changed.Repository.LastUpdate.HasDescription);
        Assert.Equal("new", changed.Repository.LastUpdate.Description);
        Assert.Equal(GroupLifecycle.Archived, changed.Repository.LastUpdate.Lifecycle);
        Assert.Equal("archive", changed.Repository.LastUpdate.Reason);
        Assert.Null(changed.Repository.LastUpdate.SupplyEvidence);

        changed.Idempotency.ReplayCompletedRequests = true;
        Result<GroupCommandOutcome> replay = await changed.Service.ExecuteAsync(
            change,
            TestContext.Current.CancellationToken);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(1, changed.Repository.UpdateCalls);

        TestEnvironment noOp = new();
        noOp.Repository.UpdateResults.Enqueue(new GroupWriteResult(
            GroupWriteDisposition.Written,
            before,
            before,
            WasChanged: false,
            CurrentVersion: before.Version));
        Result<GroupCommandOutcome> unchanged = await noOp.Service.ExecuteAsync(
            valid with { ExpectedVersion = before.Version, Name = "Before" },
            TestContext.Current.CancellationToken);
        Assert.True(unchanged.IsSuccess);
        Assert.Empty(noOp.Audit.Entries);
        Assert.Empty(noOp.Outbox.Events);
        Assert.Equal(1, noOp.UnitOfWork.CommitCalls);
        Assert.Single(noOp.Idempotency.Completions);
    }

    [Fact]
    public async Task ActivationCoversValidationPreconditionsMutationFailuresSuccessAndReplay()
    {
        EntityId groupId = EntityId.New();
        SupplyReadinessEvidence evidence = new("supply.AbC_123-x", Now);
        ActivateGroupCommand valid = ActivateCommand(groupId, evidence);

        foreach (ActivateGroupCommand invalid in new[]
                 {
                     valid with { IdempotencyKey = "bad key" },
                     valid with { ExpectedVersion = 0 },
                     valid with { Reason = "\n" },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("bad", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("supplytoken", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence(".token", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("supply.", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("Supply.token", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("supply.bad!", Now) },
                     valid with { SupplyEvidence = new SupplyReadinessEvidence("supply.token", default) },
                     valid with
                     {
                         MetadataPatch = new GroupMetadataPatch(true, "\n", false, null),
                     },
                 })
        {
            TestEnvironment environment = new();
            AssertFailure(
                await environment.Service.ActivateAsync(
                    invalid,
                    TestContext.Current.CancellationToken),
                GroupErrorCodes.ValidationFailed);
        }

        GroupResource current = Group(
            groupId,
            version: 4,
            lifecycle: GroupLifecycle.Disabled,
            hasQuota: true);
        foreach ((GroupResource? resource, SupplyReadinessEvidence? supplied, string code, int status) in new[]
                 {
                     (null, evidence, GroupErrorCodes.ResourceNotFound, 404),
                     (current with { Version = 5 }, evidence, GroupErrorCodes.VersionConflict, 412),
                     (current with { Lifecycle = GroupLifecycle.Active }, evidence, GroupErrorCodes.GroupActivationNotReady, 409),
                     (current with { HasCurrentQuotaPeriod = false }, evidence, GroupErrorCodes.GroupActivationNotReady, 409),
                     (current, null, GroupErrorCodes.GroupActivationNotReady, 409),
                 })
        {
            TestEnvironment environment = new();
            environment.Repository.ActivationResult = resource;
            Result<GroupActivationResult> preconditionResult = await environment.Service.ActivateAsync(
                valid with { SupplyEvidence = supplied },
                TestContext.Current.CancellationToken);
            AssertFailure(preconditionResult, code);
            Assert.Equal(status, preconditionResult.Error.Presentation!.Status);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        }

        foreach ((GroupWriteResult write, string code, int status) in new[]
                 {
                     (new GroupWriteResult(GroupWriteDisposition.NotFound, null), GroupErrorCodes.ResourceNotFound, 404),
                     (new GroupWriteResult(GroupWriteDisposition.VersionConflict, null, CurrentVersion: 7), GroupErrorCodes.VersionConflict, 412),
                     (new GroupWriteResult(GroupWriteDisposition.NameConflict, null), GroupErrorCodes.ResourceConflict, 409),
                     (new GroupWriteResult(GroupWriteDisposition.ActivationNotReady, null), GroupErrorCodes.GroupActivationNotReady, 409),
                 })
        {
            TestEnvironment environment = new();
            environment.Repository.ActivationResult = current;
            environment.Repository.UpdateResults.Enqueue(write);
            Result<GroupActivationResult> mutationResult = await environment.Service.ActivateAsync(
                valid,
                TestContext.Current.CancellationToken);
            AssertFailure(mutationResult, code);
            Assert.Equal(status, mutationResult.Error.Presentation!.Status);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        }

        GroupResource activated = current with
        {
            Name = "Activated",
            Description = "ready",
            Lifecycle = GroupLifecycle.Active,
            Version = 5,
            UpdatedAt = Now.AddMinutes(2),
        };
        TestEnvironment success = new();
        success.Repository.ActivationResult = current;
        success.Repository.UpdateResults.Enqueue(new GroupWriteResult(
            GroupWriteDisposition.Written,
            activated,
            current,
            WasChanged: true,
            CurrentVersion: 5));
        ActivateGroupCommand activate = valid with
        {
            MetadataPatch = new GroupMetadataPatch(
                HasName: true,
                Name: " Activated ",
                HasDescription: true,
                Description: "ready"),
        };
        Result<GroupActivationResult> result = await success.Service.ActivateAsync(
            activate,
            TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        Assert.Equal(GroupLifecycle.Active, result.Value.Lifecycle);
        Assert.Equal(activated.ToSnapshot(), result.Value.Resource);
        Assert.Equal("groupquota.group.activated", Assert.Single(success.Audit.Entries).Action);
        Assert.Equal("group_activated", Assert.Single(success.Outbox.Events).EventType);
        Assert.Equal(evidence, success.Repository.LastUpdate!.SupplyEvidence);
        Assert.Equal(groupId, success.Repository.LastUpdate.GroupId);
        Assert.Equal(4, success.Repository.LastUpdate.ExpectedVersion);
        Assert.True(success.Repository.LastUpdate.HasName);
        Assert.Equal("Activated", success.Repository.LastUpdate.Name);
        Assert.True(success.Repository.LastUpdate.HasDescription);
        Assert.Equal("ready", success.Repository.LastUpdate.Description);
        Assert.Equal(GroupLifecycle.Active, success.Repository.LastUpdate.Lifecycle);
        Assert.Equal("activate", success.Repository.LastUpdate.Reason);
        Assert.Equal(1, success.UnitOfWork.CommitCalls);

        success.Idempotency.ReplayCompletedRequests = true;
        Result<GroupActivationResult> replay = await success.Service.ActivateAsync(
            activate,
            TestContext.Current.CancellationToken);
        Assert.True(replay.IsSuccess);
        Assert.Equal(result.Value.Resource, replay.Value.Resource);
        Assert.Equal(1, success.Repository.UpdateCalls);

        GroupActivationOrchestrationCommand orchestration = new(
            activate.Actor,
            activate.GroupId,
            activate.ExpectedVersion,
            activate.IdempotencyKey,
            activate.Reason,
            activate.MetadataPatch,
            activate.RequestId,
            activate.IpAddress,
            activate.UserAgent);
        Result<GroupActivationResult?> preflightReplay = await success.Service.TryReplayAsync(
            orchestration,
            TestContext.Current.CancellationToken);
        Assert.True(preflightReplay.IsSuccess);
        Assert.NotNull(preflightReplay.Value);

        TestEnvironment acquired = new();
        Result<GroupActivationResult?> preflightAcquired = await acquired.Service.TryReplayAsync(
            orchestration,
            TestContext.Current.CancellationToken);
        Assert.True(preflightAcquired.IsSuccess);
        Assert.Null(preflightAcquired.Value);

        foreach (CommandIdempotencyAcquireResult acquire in new[]
                 {
                     CommandIdempotencyAcquireResult.Conflict,
                     CommandIdempotencyAcquireResult.Busy,
                 })
        {
            TestEnvironment environment = new();
            environment.Idempotency.NextAcquire = acquire;
            Result<GroupActivationResult?> preflight = await environment.Service.TryReplayAsync(
                orchestration,
                TestContext.Current.CancellationToken);
            AssertFailure(
                preflight,
                acquire.Disposition == CommandIdempotencyDisposition.Conflict
                    ? GroupErrorCodes.IdempotencyConflict
                    : GroupErrorCodes.CoordinationUnavailable);
        }

        AssertFailure(
            await acquired.Service.TryReplayAsync(
                orchestration with { ExpectedGroupVersion = 0 },
                TestContext.Current.CancellationToken),
            GroupErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task FailedActivationReplayAndLostLeasesFailClosed()
    {
        EntityId groupId = EntityId.New();
        ActivateGroupCommand activate = ActivateCommand(
            groupId,
            new SupplyReadinessEvidence("supply.token", Now));
        GroupResource current = Group(
            groupId,
            version: 4,
            lifecycle: GroupLifecycle.Disabled,
            hasQuota: true);
        TestEnvironment replayedFailure = new();
        replayedFailure.Repository.ActivationResult = current;
        replayedFailure.Repository.UpdateResults.Enqueue(new GroupWriteResult(
            GroupWriteDisposition.VersionConflict,
            Group: null,
            CurrentVersion: 9));

        Result<GroupActivationResult> original = await replayedFailure.Service.ActivateAsync(
            activate,
            TestContext.Current.CancellationToken);
        AssertFailure(original, GroupErrorCodes.VersionConflict);
        Assert.Equal("\"v9\"", original.Error.ETag);
        replayedFailure.Idempotency.ReplayCompletedRequests = true;

        Result<GroupActivationResult> replay = await replayedFailure.Service.ActivateAsync(
            activate,
            TestContext.Current.CancellationToken);
        AssertFailure(replay, GroupErrorCodes.VersionConflict);
        Assert.Equal(original.Error.Description, replay.Error.Description);
        Assert.Equal(original.Error.Presentation, replay.Error.Presentation);
        Assert.Equal(original.Error.ETag, replay.Error.ETag);
        Assert.Equal(1, replayedFailure.Repository.UpdateCalls);
        Assert.Equal(1, replayedFailure.UnitOfWork.CommitCalls);

        TestEnvironment failedLease = new();
        failedLease.Idempotency.CompleteSucceeds = false;
        failedLease.Repository.CreateFactory = static _ => new GroupWriteResult(
            GroupWriteDisposition.NameConflict,
            Group: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failedLease.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, failedLease.UnitOfWork.CommitCalls);

        TestEnvironment successfulLease = new();
        successfulLease.Idempotency.CompleteSucceeds = false;
        successfulLease.Repository.CreateFactory = write => new GroupWriteResult(
            GroupWriteDisposition.Written,
            Group(write.GroupId, name: write.Name, description: write.Description));
        await Assert.ThrowsAsync<InvalidOperationException>(() => successfulLease.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);
        Assert.Equal(0, successfulLease.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task MalformedReplaysInvalidDispositionsAndCursorsFailClosed()
    {
        TestEnvironment invalidCommandDisposition = new();
        invalidCommandDisposition.Idempotency.NextAcquire = new CommandIdempotencyAcquireResult(
            (CommandIdempotencyDisposition)999,
            Lease: null,
            Response: null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidCommandDisposition
            .Service.ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        EntityId groupId = EntityId.New();
        ActivateGroupCommand activation = ActivateCommand(
            groupId,
            new SupplyReadinessEvidence("supply.token", Now));
        TestEnvironment invalidActivationDisposition = new();
        invalidActivationDisposition.Idempotency.NextAcquire =
            new CommandIdempotencyAcquireResult(
                (CommandIdempotencyDisposition)999,
                Lease: null,
                Response: null);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidActivationDisposition
            .Service.ActivateAsync(activation, TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        GroupResource replayGroup = Group(
            groupId,
            version: 5,
            lifecycle: GroupLifecycle.Active);
        CommandIdempotencyResponse validCreated = SuccessReplay(replayGroup, status: 201);
        CommandIdempotencyResponse validUpdated = SuccessReplay(replayGroup, status: 200);
        TestEnvironment invalidActivationStatus = new();
        invalidActivationStatus.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(validCreated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidActivationStatus.Service
            .ActivateAsync(activation, TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        TestEnvironment createWithUpdateReplay = new();
        createWithUpdateReplay.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(validUpdated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => createWithUpdateReplay.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        TestEnvironment updateWithCreateReplay = new();
        updateWithCreateReplay.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(validCreated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updateWithCreateReplay.Service
            .ExecuteAsync(UpdateCommand(groupId), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        TestEnvironment inactiveActivationReplay = new();
        inactiveActivationReplay.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(SuccessReplay(
                replayGroup with { Lifecycle = GroupLifecycle.Disabled },
                status: 200));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inactiveActivationReplay.Service
            .ActivateAsync(activation, TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        CommandIdempotencyResponse[] invalidSuccessResponses =
        [
            validCreated with { Status = 202 },
            validCreated with
            {
                Body = ReplayBody(replayGroup, id: Guid.Empty),
            },
        ];
        foreach (CommandIdempotencyResponse response in invalidSuccessResponses)
        {
            TestEnvironment malformed = new();
            malformed.Idempotency.NextAcquire = CommandIdempotencyAcquireResult.Replay(response);
            await Assert.ThrowsAsync<InvalidOperationException>(() => malformed.Service
                .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
                .AsTask()).ConfigureAwait(true);
        }

        TestEnvironment invalidLifecycle = new();
        invalidLifecycle.Idempotency.NextAcquire = CommandIdempotencyAcquireResult.Replay(
            validCreated with
            {
                Body = ReplayBody(replayGroup with { Lifecycle = (GroupLifecycle)999 }),
            });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => invalidLifecycle.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        ResultErrorPresentation versionConflict = new(
            GroupErrorCodes.VersionConflict,
            412,
            "Version conflict",
            "The resource version no longer matches; retrieve it again before retrying.",
            Retryable: true);
        CommandIdempotencyResponse invalidETag = FailureReplay(
            412,
            "version conflict",
            versionConflict,
            etag: "\"v01\"");
        TestEnvironment malformedFailure = new();
        malformedFailure.Idempotency.NextAcquire =
            CommandIdempotencyAcquireResult.Replay(invalidETag);
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformedFailure.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        ResultErrorPresentation unsupported = new(
            "unsupported_group_failure",
            409,
            "Unsupported",
            "Unsupported",
            Retryable: false);
        TestEnvironment unsupportedFailure = new();
        unsupportedFailure.Idempotency.NextAcquire = CommandIdempotencyAcquireResult.Replay(
            FailureReplay(409, "unsupported", unsupported));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unsupportedFailure.Service
            .ExecuteAsync(CreateCommand(), TestContext.Current.CancellationToken)
            .AsTask()).ConfigureAwait(true);

        foreach (string cursor in new[]
                 {
                     Cursor(unixMicroseconds: 0, Guid.Empty),
                     Cursor(long.MaxValue, EntityId.New().Value),
                 })
        {
            TestEnvironment malformedCursor = new();
            Result<GroupPage> result = await malformedCursor.Service.ExecuteAsync(
                new ListGroupsQuery(Admin, cursor),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            AssertFailure(result, GroupErrorCodes.InvalidRequest);
        }
    }

    private static CreateGroupCommand CreateCommand() => new(
        EntityId.New(),
        Admin,
        "create-group-key",
        "Research",
        "Shared research",
        TotalTokens: 10_000,
        IpAddress: "127.0.0.1",
        UserAgent: "unit-test");

    private static UpdateGroupCommand UpdateCommand(EntityId groupId) => new(
        EntityId.New(),
        Admin,
        "update-group-key",
        groupId,
        ExpectedVersion: 4,
        HasName: true,
        Name: "Updated",
        HasDescription: false,
        Description: null,
        HasStatus: false,
        Status: null,
        Reason: null,
        IpAddress: "127.0.0.1",
        UserAgent: "unit-test");

    private static ActivateGroupCommand ActivateCommand(
        EntityId groupId,
        SupplyReadinessEvidence? evidence) => new(
            new ActorContext(Admin.UserId, Admin.TokenVersion),
            groupId,
            ExpectedVersion: 4,
            IdempotencyKey: "activate-group-key",
            Reason: "activate",
            SupplyEvidence: evidence,
            RequestId: EntityId.New(),
            IpAddress: "127.0.0.1",
            UserAgent: "unit-test");

    private static CommandIdempotencyResponse SuccessReplay(
        GroupResource group,
        int status)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["ETag"] = $"\"v{group.Version}\"",
        };
        if (status == 201)
        {
            headers["Location"] = $"/api/v1/admin/groups/{group.Id.Value:D}";
        }

        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            status,
            ReplayBody(group),
            BodyEnvelope: null,
            JsonSerializer.SerializeToElement(headers),
            "group",
            group.Id);
    }

    private static JsonElement ReplayBody(GroupResource group, Guid? id = null) =>
        JsonSerializer.SerializeToElement(new
        {
            Id = id ?? group.Id.Value,
            group.Name,
            group.Description,
            Status = group.Lifecycle,
            group.Version,
            group.CreatedAt,
            group.UpdatedAt,
        });

    private static CommandIdempotencyResponse FailureReplay(
        int status,
        string description,
        ResultErrorPresentation presentation,
        string? etag = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        if (etag is not null)
        {
            headers["ETag"] = etag;
        }

        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Failed,
            status,
            JsonSerializer.SerializeToElement(new
            {
                Description = description,
                Presentation = presentation,
            }),
            BodyEnvelope: null,
            JsonSerializer.SerializeToElement(headers),
            ResourceType: null,
            ResourceId: null);
    }

    private static string Cursor(long unixMicroseconds, Guid id)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes.Clear();
        bytes[0] = 0x01;
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(1, 8), unixMicroseconds);
        Convert.FromHexString(id.ToString("N"), bytes[9..], out _, out _);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static GroupResource Group(
        EntityId? id = null,
        long version = 1,
        string name = "Research",
        string? description = "Shared research",
        GroupLifecycle lifecycle = GroupLifecycle.Disabled,
        bool hasQuota = true) => new(
            id ?? EntityId.New(),
            name,
            description,
            lifecycle,
            version,
            Now.AddDays(-1),
            Now,
            hasQuota,
            Now);

    private static void AssertFailure<T>(Result<T> result, string code)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    private sealed class TestEnvironment
    {
        internal TestEnvironment()
        {
            Service = new GroupControlPlaneService(
                Repository,
                UnitOfWork,
                Idempotency,
                Audit,
                Outbox,
                new GroupQuotaPolicy(Enumerable.Range(1, 32)
                    .Select(static value => (byte)value)
                    .ToArray()),
                new FixedTimeProvider(Now));
        }

        internal FakeGroupRepository Repository { get; } = new();

        internal RecordingUnitOfWorkFactory UnitOfWork { get; } = new();

        internal RecordingIdempotencyStore Idempotency { get; } = new();

        internal RecordingAuditAppender Audit { get; } = new();

        internal RecordingOutboxAppender Outbox { get; } = new();

        internal GroupControlPlaneService Service { get; }
    }

    private sealed class FakeGroupRepository : IGroupRepository
    {
        internal GroupSlice ListResult { get; set; } = new([], HasMore: false);

        internal GroupCursor? LastCursor { get; private set; }

        internal GroupResource? GetResult { get; set; }

        internal GroupResource? ActivationResult { get; set; }

        internal Func<CreateGroupWrite, GroupWriteResult>? CreateFactory { get; set; }

        internal Queue<GroupWriteResult> UpdateResults { get; } = [];

        internal int CreateCalls { get; private set; }

        internal int UpdateCalls { get; private set; }

        internal CreateGroupWrite? LastCreate { get; private set; }

        internal UpdateGroupWrite? LastUpdate { get; private set; }

        public ValueTask<GroupSlice> ListAsync(
            GroupCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastCursor = cursor;
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<GroupResource?> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<GroupResource?> GetForActivationAsync(
            EntityId groupId,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ActivationResult);
        }

        public ValueTask<GroupWriteResult> CreateAsync(
            CreateGroupWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreate = write;
            return ValueTask.FromResult(
                CreateFactory?.Invoke(write)
                ?? new GroupWriteResult(GroupWriteDisposition.LifecycleConflict, null));
        }

        public ValueTask<GroupWriteResult> UpdateAsync(
            UpdateGroupWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCalls++;
            LastUpdate = write;
            return ValueTask.FromResult(UpdateResults.Count > 0
                ? UpdateResults.Dequeue()
                : new GroupWriteResult(GroupWriteDisposition.LifecycleConflict, null));
        }
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(RecordingUnitOfWorkFactory owner) : IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = new UnitOfWorkContext();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        private CommandIdempotencyRequest? _activeRequest;
        private CommandIdempotencyRequest? _completedRequest;
        private CommandIdempotencyResponse? _completedResponse;

        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        internal List<CommandIdempotencyRequest> Requests { get; } = [];

        internal bool CompleteSucceeds { get; set; } = true;

        internal bool ReplayCompletedRequests { get; set; }

        internal CommandIdempotencyAcquireResult? NextAcquire { get; set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (NextAcquire is not null)
            {
                CommandIdempotencyAcquireResult result = NextAcquire;
                NextAcquire = null;
                return ValueTask.FromResult(result);
            }

            if (ReplayCompletedRequests && _completedResponse is not null)
            {
                bool matches = _completedRequest is not null
                    && string.Equals(
                        _completedRequest.Scope,
                        request.Scope,
                        StringComparison.Ordinal)
                    && string.Equals(_completedRequest.Key, request.Key, StringComparison.Ordinal)
                    && string.Equals(
                        _completedRequest.ActorFingerprint,
                        request.ActorFingerprint,
                        StringComparison.Ordinal)
                    && _completedRequest.RequestHash.Span.SequenceEqual(request.RequestHash.Span);
                return ValueTask.FromResult(matches
                    ? CommandIdempotencyAcquireResult.Replay(_completedResponse)
                    : CommandIdempotencyAcquireResult.Conflict);
            }

            _activeRequest = request;
            return ValueTask.FromResult(CommandIdempotencyAcquireResult.Acquired(
                new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    request.Owner,
                    Generation: 1,
                    Version: 1)));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "The Group tests do not heartbeat command leases.");

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(completion);
            if (!CompleteSucceeds)
            {
                return ValueTask.FromResult(false);
            }

            _completedRequest = _activeRequest
                ?? throw new InvalidOperationException(
                    "A Group completion did not have an active idempotency request.");
            _completedResponse = new CommandIdempotencyResponse(
                completion.TerminalStatus,
                completion.ResponseStatus,
                completion.ResponseBody,
                completion.ResponseBodyEnvelope,
                completion.ResponseHeaders,
                completion.ResourceType,
                completion.ResourceId);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal List<AuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOutboxAppender : IOutboxAppender
    {
        internal List<IntegrationEvent> Events { get; } = [];

        public ValueTask AppendAsync(
            IntegrationEvent integrationEvent,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(integrationEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
#pragma warning restore MA0051
