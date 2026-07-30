#pragma warning disable MA0051 // The coverage matrix keeps each fail-closed protocol branch explicit.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed class GroupSupplyModifiedLineCoverageTests
{
    private const string ResourceType = "group_supply_configuration";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadEnforcesRoleAndFoundSemanticsAndMapsBindings()
    {
        EntityId groupId = EntityId.New();
        TestEnvironment environment = new();

        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetGroupSupplyConfigurationQuery(
                    Actor(AccountControlRole.User),
                    groupId),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetGroupSupplyConfigurationQuery(
                    Actor(AccountControlRole.Admin, tokenVersion: 0),
                    groupId),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        Assert.Equal(0, environment.Repository.GetCalls);

        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetGroupSupplyConfigurationQuery(
                    Actor(AccountControlRole.Operator),
                    groupId),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.ResourceNotFound);

        EntityId channelId = EntityId.New();
        EntityId firstAccountId = Id("00000000-0000-0000-0000-000000000001");
        EntityId secondAccountId = Id("00000000-0000-0000-0000-000000000002");
        environment.Repository.GetResult = Configuration(
            groupId,
            channelId,
            [
                Binding(firstAccountId, enabled: true, priority: -3, weight: 4),
                Binding(secondAccountId, enabled: false, priority: null, weight: null),
            ],
            version: 9);

        Result<GroupSupplyConfigurationView> result =
            await environment.Service.ExecuteAsync(
                new GetGroupSupplyConfigurationQuery(
                    Actor(AccountControlRole.Auditor),
                    groupId),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(groupId, result.Value.GroupId);
        Assert.Equal(channelId, result.Value.ChannelId);
        Assert.Equal(9, result.Value.Version);
        Assert.Collection(
            result.Value.AccountBindings,
            first =>
            {
                Assert.Equal(firstAccountId, first.AccountId);
                Assert.True(first.Enabled);
                Assert.Equal(-3, first.PriorityOverride);
                Assert.Equal(4, first.WeightOverride);
            },
            second =>
            {
                Assert.Equal(secondAccountId, second.AccountId);
                Assert.False(second.Enabled);
                Assert.Null(second.PriorityOverride);
                Assert.Null(second.WeightOverride);
            });
        Assert.Equal(2, environment.Repository.GetCalls);
    }

    [Fact]
    public async Task CreateRejectsUnauthorizedAndInvalidRequestsBeforeTransaction()
    {
        EntityId groupId = EntityId.New();
        EntityId accountId = EntityId.New();
        CreateGroupSupplyConfigurationCommand valid = CreateCommand(groupId);
        TestEnvironment environment = new();

        CreateGroupSupplyConfigurationCommand[] unauthorized =
        [
            valid with { Actor = Actor(AccountControlRole.Operator) },
            valid with { Actor = Actor(AccountControlRole.Admin, tokenVersion: 0) },
        ];
        CreateGroupSupplyConfigurationCommand[] invalid =
        [
            valid with { IdempotencyKey = " " },
            valid with { AccountBindings = null! },
            valid with
            {
                AccountBindings =
                [
                    ViewBinding(accountId, enabled: true),
                    ViewBinding(accountId, enabled: false),
                ],
            },
            valid with
            {
                AccountBindings =
                [
                    ViewBinding(
                        accountId,
                        enabled: true,
                        priority: 100001),
                ],
            },
            valid with
            {
                AccountBindings =
                [
                    ViewBinding(
                        accountId,
                        enabled: true,
                        weight: 0),
                ],
            },
        ];

        foreach (CreateGroupSupplyConfigurationCommand command in unauthorized)
        {
            AssertFailure(
                await environment.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken),
                SupplyControlErrorCodes.RoleRequired);
        }

        foreach (CreateGroupSupplyConfigurationCommand command in invalid)
        {
            AssertFailure(
                await environment.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken),
                SupplyControlErrorCodes.ValidationFailed);
        }

        Assert.Equal(0, environment.UnitOfWork.BeginCalls);
        Assert.Equal(0, environment.Repository.CreateCalls);
        Assert.Empty(environment.Idempotency.Requests);
    }

    [Fact]
    public async Task CreateReturnsCoordinationFailuresBeforeRepositoryMutation()
    {
        foreach ((CommandIdempotencyAcquireResult acquire, string code, long? retry)
                 in new[]
                 {
                     (
                         CommandIdempotencyAcquireResult.Conflict,
                         SupplyControlErrorCodes.IdempotencyConflict,
                         (long?)null),
                     (
                         CommandIdempotencyAcquireResult.Busy,
                         SupplyControlErrorCodes.CoordinationUnavailable,
                         (long?)1),
                 })
        {
            TestEnvironment environment = new();
            environment.Idempotency.AcquireResult = acquire;

            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
                await environment.Service.ExecuteAsync(
                    CreateCommand(EntityId.New()),
                    TestContext.Current.CancellationToken);

            AssertFailure(result, code);
            Assert.Equal(retry, result.Error.RetryAfterSeconds);
            Assert.Equal(0, environment.Repository.CreateCalls);
            Assert.Equal(0, environment.UnitOfWork.CommitCalls);
            Assert.Empty(environment.Idempotency.Completions);
        }
    }

    [Fact]
    public async Task CreateMapsEveryRepositoryDispositionToDurableFailure()
    {
        (GroupSupplyMutationDisposition Disposition, string Code, int Status, string? ETag)[]
            cases =
            [
                (
                    GroupSupplyMutationDisposition.ValidationFailed,
                    SupplyControlErrorCodes.ValidationFailed,
                    422,
                    null),
                (
                    GroupSupplyMutationDisposition.Conflict,
                    SupplyControlErrorCodes.ResourceConflict,
                    409,
                    null),
                (
                    GroupSupplyMutationDisposition.NotFound,
                    SupplyControlErrorCodes.ResourceNotFound,
                    404,
                    null),
                (
                    GroupSupplyMutationDisposition.VersionConflict,
                    SupplyControlErrorCodes.VersionConflict,
                    412,
                    "\"v7\""),
            ];

        foreach ((
                     GroupSupplyMutationDisposition disposition,
                     string code,
                     int status,
                     string? etag) in cases)
        {
            TestEnvironment environment = new();
            environment.Repository.CreateResults.Enqueue(new(
                disposition,
                WasChanged: false,
                Value: null,
                Before: null,
                CurrentVersion: 7));

            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
                await environment.Service.ExecuteAsync(
                    CreateCommand(EntityId.New()),
                    TestContext.Current.CancellationToken);

            AssertFailure(result, code);
            Assert.Equal(etag, result.Error.ETag);
            CommandIdempotencyCompletion completion =
                Assert.Single(environment.Idempotency.Completions);
            Assert.Equal(
                CommandIdempotencyTerminalStatus.Failed,
                completion.TerminalStatus);
            Assert.Equal(status, completion.ResponseStatus);
            Assert.Equal(
                code,
                completion.ResponseBody!.Value.GetProperty("Code").GetString());
            Assert.Equal(etag, Header(completion.ResponseHeaders, "ETag"));
            Assert.Null(completion.ResourceType);
            Assert.Null(completion.ResourceId);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
            Assert.Empty(environment.Audit.Entries);
            Assert.Empty(environment.Outbox.Events);
        }

        TestEnvironment noCurrentVersion = new();
        noCurrentVersion.Repository.CreateResults.Enqueue(new(
            GroupSupplyMutationDisposition.VersionConflict,
            WasChanged: false,
            Value: null,
            Before: null,
            CurrentVersion: null));
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> missingVersion =
            await noCurrentVersion.Service.ExecuteAsync(
                CreateCommand(EntityId.New()),
                TestContext.Current.CancellationToken);
        AssertFailure(missingVersion, SupplyControlErrorCodes.VersionConflict);
        Assert.Null(missingVersion.Error.ETag);
    }

    [Fact]
    public async Task CreateWithBindingsEmitsMappedAuditEventAndReplayEnvelope()
    {
        EntityId groupId = EntityId.New();
        EntityId channelId = EntityId.New();
        EntityId firstAccountId = Id("00000000-0000-0000-0000-000000000001");
        EntityId secondAccountId = Id("00000000-0000-0000-0000-000000000002");
        TestEnvironment environment = new();
        GroupSupplyConfigurationResource resource = Configuration(
            groupId,
            channelId,
            [
                Binding(firstAccountId, enabled: true, priority: 8, weight: 70),
                Binding(secondAccountId, enabled: false, priority: null, weight: null),
            ],
            version: 3);
        environment.Repository.CreateResults.Enqueue(Written(resource));
        CreateGroupSupplyConfigurationCommand command = CreateCommand(groupId) with
        {
            ChannelId = channelId,
            AccountBindings =
            [
                ViewBinding(firstAccountId, enabled: true, priority: 8, weight: 70),
                ViewBinding(secondAccountId, enabled: false),
            ],
            IpAddress = "127.0.0.1",
            UserAgent = "coverage-test",
        };

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(201, result.Value.StatusCode);
        Assert.False(result.Value.IsReplay);
        Assert.Equal("\"v3\"", result.Value.ETag);
        Assert.Equal(channelId, result.Value.Value.ChannelId);
        Assert.Equal(2, result.Value.Value.AccountBindings.Count);

        GroupSupplyConfigurationCreateWrite write =
            Assert.IsType<GroupSupplyConfigurationCreateWrite>(
                environment.Repository.LastCreate);
        Assert.Equal(channelId, write.ChannelId);
        Assert.Equal(2, write.AccountBindings.Count);
        AuditEntry audit = Assert.Single(environment.Audit.Entries);
        Assert.Equal("supply.group_configuration.created", audit.Action);
        Assert.Null(audit.BeforeState);
        JsonElement after = audit.AfterState!.Value;
        Assert.Equal(channelId.Value, after.GetProperty("channel_id").GetGuid());
        Assert.Equal(
            2,
            after.GetProperty("account_bindings").GetArrayLength());
        Assert.DoesNotContain(
            command.IdempotencyKey,
            audit.Metadata.GetRawText(),
            StringComparison.Ordinal);

        IntegrationEvent integrationEvent =
            Assert.Single(environment.Outbox.Events);
        Assert.Equal(
            "group_supply_configuration_created",
            integrationEvent.EventType);
        Assert.Equal(
            1,
            integrationEvent.Payload
                .GetProperty("enabled_binding_count")
                .GetInt32());
        CommandIdempotencyCompletion completion =
            Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(ResourceType, completion.ResourceType);
        Assert.Equal(groupId, completion.ResourceId);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task PatchRejectsRolePresenceAndValueViolationsBeforeTransaction()
    {
        EntityId groupId = EntityId.New();
        EntityId accountId = EntityId.New();
        PatchGroupSupplyConfigurationCommand valid = PatchCommand(groupId);
        TestEnvironment environment = new();
        PatchGroupSupplyConfigurationCommand[] invalid =
        [
            valid with { Actor = Actor(AccountControlRole.Operator) },
            valid with { Actor = Actor(AccountControlRole.Admin, tokenVersion: 0) },
            valid with { IdempotencyKey = " " },
            valid with { ExpectedVersion = 0 },
            valid with { Reason = "invalid\nreason" },
            valid with
            {
                ChannelSpecified = false,
                ChannelId = null,
                AccountBindingsSpecified = false,
                AccountBindings = null,
            },
            valid with
            {
                AccountBindingsSpecified = true,
                AccountBindings = null,
            },
            valid with
            {
                AccountBindingsSpecified = false,
                AccountBindings =
                [
                    ViewBinding(accountId, enabled: true),
                ],
            },
            valid with
            {
                ChannelSpecified = false,
                ChannelId = EntityId.New(),
                AccountBindingsSpecified = true,
                AccountBindings = [],
            },
            valid with
            {
                ChannelSpecified = false,
                ChannelId = null,
                AccountBindingsSpecified = true,
                AccountBindings =
                [
                    ViewBinding(accountId, enabled: true),
                    ViewBinding(accountId, enabled: false),
                ],
            },
        ];

        foreach (PatchGroupSupplyConfigurationCommand command in invalid)
        {
            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
                await environment.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken);
            AssertFailure(
                result,
                command.Actor.Role != AccountControlRole.Admin
                    || command.Actor.TokenVersion <= 0
                    ? SupplyControlErrorCodes.RoleRequired
                    : SupplyControlErrorCodes.ValidationFailed);
        }

        Assert.Equal(0, environment.UnitOfWork.BeginCalls);
        Assert.Equal(0, environment.Repository.PatchCalls);
        Assert.Empty(environment.Idempotency.Requests);
    }

    [Fact]
    public async Task PatchHandlesEarlyFailureDispositionAndNoChange()
    {
        EntityId groupId = EntityId.New();

        TestEnvironment early = new();
        early.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Busy;
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> busy =
            await early.Service.ExecuteAsync(
                PatchCommand(groupId),
                TestContext.Current.CancellationToken);
        AssertFailure(busy, SupplyControlErrorCodes.CoordinationUnavailable);
        Assert.Equal(0, early.Repository.PatchCalls);

        TestEnvironment failed = new();
        failed.Repository.PatchResults.Enqueue(new(
            GroupSupplyMutationDisposition.NotFound,
            WasChanged: false,
            Value: null,
            Before: null));
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> notFound =
            await failed.Service.ExecuteAsync(
                PatchCommand(groupId),
                TestContext.Current.CancellationToken);
        AssertFailure(notFound, SupplyControlErrorCodes.ResourceNotFound);
        Assert.Equal(1, failed.UnitOfWork.CommitCalls);
        Assert.Empty(failed.Audit.Entries);
        Assert.Empty(failed.Outbox.Events);

        GroupSupplyConfigurationResource unchanged =
            Configuration(groupId, channelId: null, [], version: 5);
        TestEnvironment noChange = new();
        noChange.Repository.PatchResults.Enqueue(new(
            GroupSupplyMutationDisposition.Written,
            WasChanged: false,
            unchanged,
            unchanged,
            unchanged.Version));
        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await noChange.Service.ExecuteAsync(
                PatchCommand(groupId) with { ExpectedVersion = 5 },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v5\"", result.Value.ETag);
        Assert.Empty(noChange.Audit.Entries);
        Assert.Empty(noChange.Outbox.Events);
        Assert.Single(noChange.Idempotency.Completions);
        Assert.Equal(1, noChange.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task PatchMaterialChangeCapturesBeforeAndAfterBindingState()
    {
        EntityId groupId = EntityId.New();
        EntityId accountId = EntityId.New();
        GroupSupplyConfigurationResource before = Configuration(
            groupId,
            channelId: null,
            [Binding(accountId, enabled: false, priority: null, weight: null)],
            version: 2);
        GroupSupplyConfigurationResource after = before with
        {
            AccountBindings =
            [
                Binding(accountId, enabled: true, priority: 12, weight: 90),
            ],
            Version = 4,
            UpdatedAt = Now.AddMinutes(1),
        };
        TestEnvironment environment = new();
        environment.Repository.PatchResults.Enqueue(Written(after, before));

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
            await environment.Service.ExecuteAsync(
                PatchCommand(groupId) with
                {
                    ExpectedVersion = 2,
                    ChannelSpecified = false,
                    ChannelId = null,
                    AccountBindingsSpecified = true,
                    AccountBindings =
                    [
                        ViewBinding(
                            accountId,
                            enabled: true,
                            priority: 12,
                            weight: 90),
                    ],
                    Reason = "  enable primary account  ",
                },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v4\"", result.Value.ETag);
        GroupSupplyConfigurationPatchWrite write =
            Assert.IsType<GroupSupplyConfigurationPatchWrite>(
                environment.Repository.LastPatch);
        Assert.Equal("enable primary account", write.Reason);
        Assert.True(write.AccountBindingsSpecified);
        Assert.True(Assert.Single(write.AccountBindings!).Enabled);
        AuditEntry audit = Assert.Single(environment.Audit.Entries);
        Assert.Equal("supply.group_configuration.updated", audit.Action);
        Assert.NotNull(audit.BeforeState);
        Assert.False(
            audit.BeforeState!.Value
                .GetProperty("account_bindings")[0]
                .GetProperty("enabled")
                .GetBoolean());
        Assert.True(
            audit.AfterState!.Value
                .GetProperty("account_bindings")[0]
                .GetProperty("enabled")
                .GetBoolean());
        Assert.Equal(
            "group_supply_configuration_updated",
            Assert.Single(environment.Outbox.Events).EventType);
    }

    [Fact]
    public async Task InvalidRepositorySuccessAndDispositionFailClosed()
    {
        TestEnvironment missingValue = new();
        missingValue.Repository.CreateResults.Enqueue(new(
            GroupSupplyMutationDisposition.Written,
            WasChanged: true,
            Value: null,
            Before: null,
            CurrentVersion: 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            missingValue.Service.ExecuteAsync(
                    CreateCommand(EntityId.New()),
                    TestContext.Current.CancellationToken)
                .AsTask());

        TestEnvironment unknownDisposition = new();
        unknownDisposition.Repository.CreateResults.Enqueue(new(
            (GroupSupplyMutationDisposition)999,
            WasChanged: false,
            Value: null,
            Before: null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unknownDisposition.Service.ExecuteAsync(
                    CreateCommand(EntityId.New()),
                    TestContext.Current.CancellationToken)
                .AsTask());
    }

    [Fact]
    public async Task GroupSupplyReplayRoundTripsCreateAndPatchShapes()
    {
        EntityId groupId = Id("10000000-0000-0000-0000-000000000001");
        EntityId channelId = Id("20000000-0000-0000-0000-000000000001");
        EntityId firstAccountId = Id("30000000-0000-0000-0000-000000000001");
        EntityId secondAccountId = Id("30000000-0000-0000-0000-000000000002");
        GroupSupplyConfigurationView createView = new(
            groupId,
            channelId,
            [
                ViewBinding(firstAccountId, enabled: true, priority: -1, weight: 20),
                ViewBinding(secondAccountId, enabled: false),
            ],
            Version: 7,
            CreatedAt: Now,
            UpdatedAt: Now.AddMinutes(1));
        TestEnvironment environment = new();
        string location =
            $"/api/v1/admin/groups/{groupId.Value:D}/supply-configuration";
        await environment.Coordinator.CompleteSuccessAsync(
            Lease(),
            status: 201,
            createView,
            etag: "\"v7\"",
            location,
            ResourceType,
            groupId,
            environment.StandaloneUnitOfWork,
            TestContext.Current.CancellationToken);

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> createReplay =
            Assert.IsType<Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>>(
                GroupSupplyCommandCoordinator
                    .ReplayOrFailure<GroupSupplyConfigurationView>(
                        CommandIdempotencyAcquireResult.Replay(
                            Response(environment.Idempotency.Completions[0])),
                        expectedStatus: 201,
                        ResourceType,
                        groupId));

        Assert.True(createReplay.IsSuccess);
        Assert.True(createReplay.Value.IsReplay);
        Assert.Equal(location, createReplay.Value.Location);
        Assert.Equal(channelId, createReplay.Value.Value.ChannelId);
        Assert.Collection(
            createReplay.Value.Value.AccountBindings,
            first =>
            {
                Assert.Equal(firstAccountId, first.AccountId);
                Assert.True(first.Enabled);
                Assert.Equal(-1, first.PriorityOverride);
                Assert.Equal(20, first.WeightOverride);
            },
            second =>
            {
                Assert.Equal(secondAccountId, second.AccountId);
                Assert.False(second.Enabled);
            });

        GroupSupplyConfigurationView patchView = createView with
        {
            ChannelId = null,
            AccountBindings =
            [
                ViewBinding(secondAccountId, enabled: true, weight: 40),
            ],
            Version = 8,
            UpdatedAt = Now.AddMinutes(2),
        };
        await environment.Coordinator.CompleteSuccessAsync(
            Lease(),
            status: 200,
            patchView,
            etag: "\"v8\"",
            location: null,
            ResourceType,
            groupId,
            environment.StandaloneUnitOfWork,
            TestContext.Current.CancellationToken);

        Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> patchReplay =
            Assert.IsType<Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>>(
                GroupSupplyCommandCoordinator
                    .ReplayOrFailure<GroupSupplyConfigurationView>(
                        CommandIdempotencyAcquireResult.Replay(
                            Response(environment.Idempotency.Completions[1])),
                        expectedStatus: 200,
                        ResourceType,
                        groupId));

        Assert.True(patchReplay.IsSuccess);
        Assert.True(patchReplay.Value.IsReplay);
        Assert.Null(patchReplay.Value.Location);
        Assert.Null(patchReplay.Value.Value.ChannelId);
        Assert.Equal(8, patchReplay.Value.Value.Version);
        Assert.True(Assert.Single(patchReplay.Value.Value.AccountBindings).Enabled);
    }

    [Fact]
    public void GroupSupplyReplayRejectsInvalidPersistedBodies()
    {
        Guid groupId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid channelId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid accountId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        object bindings = ReplayBindings(accountId);
        JsonElement[] invalidBodies =
        [
            GroupReplayBody(
                Guid.Empty,
                channelId,
                bindings,
                version: 1,
                Now,
                Now),
            GroupReplayBody(
                groupId,
                Guid.Empty,
                bindings,
                version: 1,
                Now,
                Now),
            GroupReplayBody(
                groupId,
                channelId,
                bindings: null,
                version: 1,
                Now,
                Now),
            GroupReplayBody(
                groupId,
                channelId,
                ReplayBindings(Guid.Empty),
                version: 1,
                Now,
                Now),
            GroupReplayBody(
                groupId,
                channelId,
                bindings,
                version: 0,
                Now,
                Now),
            GroupReplayBody(
                groupId,
                channelId,
                bindings,
                version: 1,
                CreatedAt: default,
                Now),
            GroupReplayBody(
                groupId,
                channelId,
                bindings,
                version: 1,
                Now,
                UpdatedAt: default),
        ];

        foreach (JsonElement body in invalidBodies)
        {
            CommandIdempotencyResponse response = new(
                CommandIdempotencyTerminalStatus.Completed,
                Status: 200,
                Body: body,
                BodyEnvelope: null,
                Headers: Headers("\"v1\""),
                ResourceType,
                ResourceId: new EntityId(groupId));
            Assert.Throws<InvalidOperationException>(() =>
                GroupSupplyCommandCoordinator
                    .ReplayOrFailure<GroupSupplyConfigurationView>(
                        CommandIdempotencyAcquireResult.Replay(response),
                        expectedStatus: 200,
                        ResourceType,
                        new EntityId(groupId)));
        }
    }

    [Fact]
    public void CoordinatorRejectsUnknownDispositionAndMalformedFailureReplay()
    {
        CommandIdempotencyAcquireResult unknown = new(
            (CommandIdempotencyDisposition)999,
            Lease: null,
            Response: null);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupSupplyCommandCoordinator
                .ReplayOrFailure<GroupSupplyConfigurationView>(
                    unknown,
                    expectedStatus: 200,
                    ResourceType));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                unknown,
                resourceType: "channel",
                EntityId.New()));

        Result<SupplyCommandOutcome> conflict =
            Assert.IsType<Result<SupplyCommandOutcome>>(
                GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                    CommandIdempotencyAcquireResult.Conflict,
                    resourceType: "channel",
                    EntityId.New()));
        AssertFailure(conflict, SupplyControlErrorCodes.IdempotencyConflict);
        Result<SupplyCommandOutcome> busy =
            Assert.IsType<Result<SupplyCommandOutcome>>(
                GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                    CommandIdempotencyAcquireResult.Busy,
                    resourceType: "channel",
                    EntityId.New()));
        AssertFailure(busy, SupplyControlErrorCodes.CoordinationUnavailable);
        Assert.Equal(1, busy.Error.RetryAfterSeconds);

        foreach (string code in new[]
                 {
                     SupplyControlErrorCodes.ValidationFailed,
                     SupplyControlErrorCodes.ResourceConflict,
                     SupplyControlErrorCodes.ResourceNotFound,
                 })
        {
            Result<SupplyCommandOutcome<GroupSupplyConfigurationView>> result =
                Assert.IsType<
                    Result<SupplyCommandOutcome<GroupSupplyConfigurationView>>>(
                    GroupSupplyCommandCoordinator
                        .ReplayOrFailure<GroupSupplyConfigurationView>(
                            CommandIdempotencyAcquireResult.Replay(
                                FailedResponse(code, "persisted failure")),
                            expectedStatus: 200,
                            ResourceType));
            AssertFailure(result, code);
        }

        CommandIdempotencyResponse malformed = FailedResponse(
            SupplyControlErrorCodes.ValidationFailed,
            "persisted failure") with
        {
            ResourceType = ResourceType,
        };
        Assert.Throws<InvalidOperationException>(() =>
            GroupSupplyCommandCoordinator
                .ReplayOrFailure<GroupSupplyConfigurationView>(
                    CommandIdempotencyAcquireResult.Replay(malformed),
                    expectedStatus: 200,
                    ResourceType));

        Assert.Throws<InvalidOperationException>(() =>
            GroupSupplyCommandCoordinator
                .ReplayOrFailure<GroupSupplyConfigurationView>(
                    CommandIdempotencyAcquireResult.Replay(new(
                        CommandIdempotencyTerminalStatus.Failed,
                        Status: 500,
                        Body: JsonSerializer.SerializeToElement(
                            new SupplyReplayFailure(
                                "unsupported_failure",
                                "invalid")),
                        BodyEnvelope: null,
                        Headers: Headers(etag: null),
                        ResourceType: null,
                        ResourceId: null)),
                    expectedStatus: 200,
                    ResourceType));
    }

    [Fact]
    public async Task CoordinatorCompletionLossAndUnsupportedValuesFailClosed()
    {
        TestEnvironment failure = new();
        failure.Idempotency.CompleteResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failure.Coordinator.CompleteFailureAsync<string>(
                    Lease(),
                    new SupplyMutationFailure(
                        Status: 422,
                        SupplyControlErrorCodes.ValidationFailed,
                        "invalid"),
                    failure.StandaloneUnitOfWork,
                    TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(0, failure.StandaloneUnitOfWork.CommitCalls);

        TestEnvironment retire = new();
        retire.Idempotency.CompleteResult = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            retire.Coordinator.CompleteRetireAsync(
                    Lease(),
                    "\"v2\"",
                    "channel",
                    EntityId.New(),
                    retire.StandaloneUnitOfWork,
                    TestContext.Current.CancellationToken)
                .AsTask());
        Assert.Equal(0, retire.StandaloneUnitOfWork.CommitCalls);

        TestEnvironment unsupported = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unsupported.Coordinator.CompleteSuccessAsync(
                    Lease(),
                    status: 200,
                    value: "unsupported replay value",
                    etag: "\"v1\"",
                    location: null,
                    resourceType: "unsupported",
                    EntityId.New(),
                    unsupported.StandaloneUnitOfWork,
                    TestContext.Current.CancellationToken)
                .AsTask());

        CommandIdempotencyResponse channelWithUnsupportedStatus =
            ChannelResponse(
                Id("40000000-0000-0000-0000-000000000001"),
                provider: "openai_compatible",
                status: "disabled",
                expectedStatus: 202);
        Assert.Throws<InvalidOperationException>(() =>
            GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Replay(
                    channelWithUnsupportedStatus),
                expectedStatus: 202,
                resourceType: "channel"));

        TestEnvironment invalidActor = new();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            invalidActor.Coordinator.AppendAuditAsync(
                new AccountActor(
                    EntityId.New(),
                    (AccountControlRole)999,
                    TokenVersion: 1),
                "supply.invalid",
                ResourceType,
                EntityId.New(),
                EntityId.New(),
                reason: null,
                ipAddress: null,
                userAgent: null,
                before: null,
                after: null,
                idempotencyKey: "key",
                invalidActor.StandaloneUnitOfWork.Context,
                TestContext.Current.CancellationToken).AsTask().GetAwaiter().GetResult();
        });
    }

    [Fact]
    public async Task ChannelReplayVariantsCoverCoordinatorEnumAndValidationBoundaries()
    {
        EntityId channelId = Id("40000000-0000-0000-0000-000000000001");
        TestEnvironment serialized = new();
        await serialized.Coordinator.CompleteSuccessAsync(
            Lease(),
            status: 200,
            Channel(
                channelId,
                UpstreamProvider.OpenAi,
                ChannelLifecycle.Retired),
            etag: "\"v3\"",
            location: null,
            resourceType: "channel",
            channelId,
            serialized.StandaloneUnitOfWork,
            TestContext.Current.CancellationToken);

        foreach (ChannelView invalid in new[]
                 {
                     Channel(
                         channelId,
                         (UpstreamProvider)999,
                         ChannelLifecycle.Disabled),
                     Channel(
                         channelId,
                         UpstreamProvider.OpenAiCompatible,
                         (ChannelLifecycle)999),
                 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                new TestEnvironment().Coordinator.CompleteSuccessAsync(
                        Lease(),
                        status: 200,
                        invalid,
                        etag: "\"v3\"",
                        location: null,
                        resourceType: "channel",
                        channelId,
                        new RecordingUnitOfWork(),
                        TestContext.Current.CancellationToken)
                    .AsTask());
        }

        foreach ((string provider, string lifecycle) in new[]
                 {
                     ("openai", "active"),
                     ("openai_compatible", "retired"),
                 })
        {
            Result<SupplyCommandOutcome<ChannelView>> result =
                Assert.IsType<Result<SupplyCommandOutcome<ChannelView>>>(
                    GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                        CommandIdempotencyAcquireResult.Replay(
                            ChannelResponse(
                                channelId,
                                provider,
                                lifecycle,
                                expectedStatus: 200)),
                        expectedStatus: 200,
                        resourceType: "channel",
                        channelId));
            Assert.True(result.IsSuccess);
        }

        foreach (CommandIdempotencyResponse invalid in new[]
                 {
                     ChannelResponse(
                         channelId,
                         provider: "unsupported",
                         status: "disabled",
                         expectedStatus: 200),
                     ChannelResponse(
                         channelId,
                         provider: "openai_compatible",
                         status: "unsupported",
                         expectedStatus: 200),
                     ChannelResponseWithBodyId(
                         Guid.Empty,
                         channelId,
                         provider: "openai_compatible",
                         status: "disabled",
                         expectedStatus: 200),
                 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                    CommandIdempotencyAcquireResult.Replay(invalid),
                    expectedStatus: 200,
                    resourceType: "channel",
                    channelId));
        }
    }

    private static CreateGroupSupplyConfigurationCommand CreateCommand(
        EntityId groupId) => new(
        EntityId.New(),
        Actor(AccountControlRole.Admin),
        "group-supply-create-key",
        groupId,
        ChannelId: null,
        AccountBindings: [],
        IpAddress: null,
        UserAgent: null);

    private static PatchGroupSupplyConfigurationCommand PatchCommand(
        EntityId groupId) => new(
        EntityId.New(),
        Actor(AccountControlRole.Admin),
        "group-supply-patch-key",
        groupId,
        ExpectedVersion: 1,
        ChannelSpecified: true,
        ChannelId: null,
        AccountBindingsSpecified: false,
        AccountBindings: null,
        Reason: "clear channel",
        IpAddress: null,
        UserAgent: null);

    private static AccountActor Actor(
        AccountControlRole role,
        long tokenVersion = 1) => new(
        EntityId.New(),
        role,
        tokenVersion);

    private static EntityId Id(string value) =>
        new(Guid.Parse(value));

    private static GroupSupplyBindingValue Binding(
        EntityId accountId,
        bool enabled,
        int? priority,
        int? weight) => new(
        accountId,
        enabled,
        priority,
        weight);

    private static GroupSupplyBindingView ViewBinding(
        EntityId accountId,
        bool enabled,
        int? priority = null,
        int? weight = null) => new(
        accountId,
        enabled,
        priority,
        weight);

    private static GroupSupplyConfigurationResource Configuration(
        EntityId groupId,
        EntityId? channelId,
        IReadOnlyList<GroupSupplyBindingValue> bindings,
        long version) => new(
        groupId,
        channelId,
        bindings,
        version,
        Now,
        Now);

    private static GroupSupplyMutationResult Written(
        GroupSupplyConfigurationResource value,
        GroupSupplyConfigurationResource? before = null) => new(
        GroupSupplyMutationDisposition.Written,
        WasChanged: true,
        value,
        before,
        value.Version);

    private static CommandIdempotencyLease Lease() => new(
        "scope",
        "key",
        EntityId.New(),
        Generation: 1,
        Version: 1);

    private static CommandIdempotencyResponse Response(
        CommandIdempotencyCompletion completion) => new(
        completion.TerminalStatus,
        completion.ResponseStatus,
        completion.ResponseBody,
        completion.ResponseBodyEnvelope,
        completion.ResponseHeaders,
        completion.ResourceType,
        completion.ResourceId);

    private static CommandIdempotencyResponse FailedResponse(
        string code,
        string description)
    {
        int status = code switch
        {
            SupplyControlErrorCodes.ValidationFailed => 422,
            SupplyControlErrorCodes.ResourceConflict => 409,
            SupplyControlErrorCodes.ResourceNotFound => 404,
            SupplyControlErrorCodes.VersionConflict => 412,
            SupplyControlErrorCodes.ChannelInUse => 409,
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        };
        string? etag = status == 412 ? "\"v2\"" : null;
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Failed,
            status,
            JsonSerializer.SerializeToElement(
                new SupplyReplayFailure(code, description)),
            BodyEnvelope: null,
            Headers(etag),
            ResourceType: null,
            ResourceId: null);
    }

    private static JsonElement GroupReplayBody(
        Guid groupId,
        Guid? channelId,
        object? bindings,
        long version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt) =>
        JsonSerializer.SerializeToElement(new
        {
            GroupId = groupId,
            ChannelId = channelId,
            AccountBindings = bindings,
            Version = version,
            CreatedAt,
            UpdatedAt,
        });

    private static object[] ReplayBindings(Guid accountId) =>
    [
        new
        {
            AccountId = accountId,
            Enabled = true,
            PriorityOverride = (int?)4,
            WeightOverride = (int?)80,
        },
    ];

    private static ChannelView Channel(
        EntityId channelId,
        UpstreamProvider provider,
        ChannelLifecycle status) => new(
        channelId,
        "Channel",
        provider,
        status,
        new ChannelCapabilitiesSnapshot(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true),
        [new ChannelModelMappingView("model-a", "upstream-a")],
        Version: 3,
        CreatedAt: Now,
        UpdatedAt: Now);

    private static CommandIdempotencyResponse ChannelResponse(
        EntityId bodyChannelId,
        string provider,
        string status,
        int expectedStatus,
        EntityId? responseResourceId = null)
    {
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Id = bodyChannelId.Value,
            Name = "Channel",
            Provider = provider,
            Status = status,
            Capabilities = new ChannelCapabilitiesSnapshot(
                Responses: true,
                ChatCompletions: true,
                FunctionTools: true,
                Streaming: true),
            ModelMappings = new[]
            {
                new
                {
                    ClientModel = "model-a",
                    UpstreamModel = "upstream-a",
                },
            },
            Version = 3,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            expectedStatus,
            body,
            BodyEnvelope: null,
            Headers("\"v3\""),
            ResourceType: "channel",
            responseResourceId ?? bodyChannelId);
    }

    private static CommandIdempotencyResponse ChannelResponseWithBodyId(
        Guid bodyChannelId,
        EntityId responseResourceId,
        string provider,
        string status,
        int expectedStatus)
    {
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            Id = bodyChannelId,
            Name = "Channel",
            Provider = provider,
            Status = status,
            Capabilities = new ChannelCapabilitiesSnapshot(
                Responses: true,
                ChatCompletions: true,
                FunctionTools: true,
                Streaming: true),
            ModelMappings = new[]
            {
                new
                {
                    ClientModel = "model-a",
                    UpstreamModel = "upstream-a",
                },
            },
            Version = 3,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        return new CommandIdempotencyResponse(
            CommandIdempotencyTerminalStatus.Completed,
            expectedStatus,
            body,
            BodyEnvelope: null,
            Headers("\"v3\""),
            ResourceType: "channel",
            responseResourceId);
    }

    private static JsonElement Headers(string? etag, string? location = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        if (etag is not null)
        {
            headers["ETag"] = etag;
        }

        if (location is not null)
        {
            headers["Location"] = location;
        }

        return JsonSerializer.SerializeToElement(headers);
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;

    private static void AssertFailure<T>(Result<T> result, string code)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    private sealed class TestEnvironment
    {
        internal TestEnvironment()
        {
            Coordinator = new GroupSupplyCommandCoordinator(
                Idempotency,
                Audit,
                Outbox,
                new AccountControlPlanePolicy(
                    Enumerable.Range(1, 32)
                        .Select(static value => (byte)value)
                        .ToArray()));
            Service = new GroupSupplyControlPlaneService(
                Repository,
                UnitOfWork,
                Coordinator);
        }

        internal FakeRepository Repository { get; } = new();

        internal RecordingUnitOfWorkFactory UnitOfWork { get; } = new();

        internal RecordingIdempotencyStore Idempotency { get; } = new();

        internal RecordingAuditAppender Audit { get; } = new();

        internal RecordingOutboxAppender Outbox { get; } = new();

        internal RecordingUnitOfWork StandaloneUnitOfWork { get; } = new();

        internal GroupSupplyCommandCoordinator Coordinator { get; }

        internal GroupSupplyControlPlaneService Service { get; }
    }

    private sealed class FakeRepository : IGroupSupplyConfigurationRepository
    {
        internal GroupSupplyConfigurationResource? GetResult { get; set; }

        internal int GetCalls { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int PatchCalls { get; private set; }

        internal GroupSupplyConfigurationCreateWrite? LastCreate { get; private set; }

        internal GroupSupplyConfigurationPatchWrite? LastPatch { get; private set; }

        internal Queue<GroupSupplyMutationResult> CreateResults { get; } = [];

        internal Queue<GroupSupplyMutationResult> PatchResults { get; } = [];

        public ValueTask<GroupSupplyConfigurationResource?> GetAsync(
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<GroupSupplyMutationResult> CreateAsync(
            GroupSupplyConfigurationCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreate = write;
            return ValueTask.FromResult(CreateResults.Dequeue());
        }

        public ValueTask<GroupSupplyMutationResult> PatchAsync(
            GroupSupplyConfigurationPatchWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PatchCalls++;
            LastPatch = write;
            return ValueTask.FromResult(PatchResults.Dequeue());
        }
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(RecordingUnitOfWorkFactory owner) :
            IUnitOfWork
        {
            public IUnitOfWorkContext Context { get; } = new ContextValue();

            public ValueTask CommitAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.CommitCalls++;
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCalls++;
                return ValueTask.CompletedTask;
            }

            private sealed class ContextValue : IUnitOfWorkContext;
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public IUnitOfWorkContext Context { get; } = new ContextValue();

        internal int CommitCalls { get; private set; }

        public ValueTask CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class ContextValue : IUnitOfWorkContext;
    }

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        internal CommandIdempotencyAcquireResult? AcquireResult { get; set; }

        internal bool CompleteResult { get; set; } = true;

        internal List<CommandIdempotencyRequest> Requests { get; } = [];

        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(
                AcquireResult
                ?? CommandIdempotencyAcquireResult.Acquired(
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
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Supply control-plane commands do not heartbeat.");

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(completion);
            return ValueTask.FromResult(CompleteResult);
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
}
#pragma warning restore MA0051
