#pragma warning disable MA0051 // Focused control-plane fixtures keep protocol assertions readable.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Domain;

namespace PoolAI.UnitTests;

public sealed class ChannelControlPlaneServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadsEnforceRbacPaginationAndFoundSemantics()
    {
        EntityId firstId = EntityId.New();
        EntityId secondId = EntityId.New();
        ChannelResource first = Channel(firstId, "First", version: 3);
        ChannelResource second = Channel(
            secondId,
            "Second",
            version: 4,
            createdAt: Now.AddMinutes(1));
        TestEnvironment environment = new();
        environment.Repository.ListResult = new ChannelSlice(
            [first, second],
            HasMore: true);
        environment.Repository.GetResult = second;

        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListChannelsQuery(Actor(AccountControlRole.User), null),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListChannelsQuery(
                    Actor(AccountControlRole.Admin, tokenVersion: 0),
                    null),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListChannelsQuery(Actor(AccountControlRole.Admin), null, 0),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.InvalidRequest);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new ListChannelsQuery(
                    Actor(AccountControlRole.Admin),
                    "not-a-canonical-cursor",
                    50),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.InvalidRequest);
        Assert.Equal(0, environment.Repository.ListCalls);

        Result<ChannelPage> page = await environment.Service.ExecuteAsync(
            new ListChannelsQuery(
                Actor(AccountControlRole.Auditor),
                Cursor: null,
                Limit: 2),
            TestContext.Current.CancellationToken);

        Assert.True(page.IsSuccess);
        Assert.True(page.Value.HasMore);
        Assert.NotNull(page.Value.NextCursor);
        Assert.Equal([firstId, secondId], page.Value.Data.Select(static item => item.Id));
        Assert.Equal(ChannelLifecycle.Disabled, page.Value.Data[0].Status);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, page.Value.Data[0].Provider);
        Assert.Equal("model-a", page.Value.Data[0].ModelMappings[0].ClientModel);

        environment.Repository.ListResult = new ChannelSlice([], HasMore: false);
        Result<ChannelPage> nextPage = await environment.Service.ExecuteAsync(
            new ListChannelsQuery(
                Actor(AccountControlRole.Operator),
                page.Value.NextCursor,
                Limit: 2),
            TestContext.Current.CancellationToken);

        Assert.True(nextPage.IsSuccess);
        Assert.Null(nextPage.Value.NextCursor);
        Assert.Equal(secondId, environment.Repository.LastCursor!.Id);
        Assert.Equal(second.CreatedAt, environment.Repository.LastCursor.CreatedAt);

        Result<ChannelView> found = await environment.Service.ExecuteAsync(
            new GetChannelQuery(Actor(AccountControlRole.Operator), secondId),
            TestContext.Current.CancellationToken);
        Assert.True(found.IsSuccess);
        Assert.Equal(secondId, found.Value.Id);
        Assert.Equal(4, found.Value.Version);

        environment.Repository.GetResult = null;
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetChannelQuery(Actor(AccountControlRole.Admin), secondId),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.ResourceNotFound);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                new GetChannelQuery(Actor(AccountControlRole.User), secondId),
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
    }

    [Fact]
    public async Task CreateRejectsUnauthorizedAndInvalidRequestsBeforeOpeningTransaction()
    {
        CreateChannelCommand valid = CreateCommand();
        TestEnvironment environment = new();
        CreateChannelCommand[] invalid =
        [
            valid with { IdempotencyKey = " " },
            valid with { Name = "\u0001" },
            valid with { Provider = (UpstreamProvider)999 },
            valid with { Capabilities = null! },
            valid with { ModelMappings = [] },
            valid with
            {
                ModelMappings =
                [
                    new ChannelModelMappingView("model-a", "upstream-a"),
                    new ChannelModelMappingView("model-a", "upstream-b"),
                ],
            },
        ];

        AssertFailure(
            await environment.Service.ExecuteAsync(
                valid with { Actor = Actor(AccountControlRole.Auditor) },
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        AssertFailure(
            await environment.Service.ExecuteAsync(
                valid with
                {
                    Actor = Actor(AccountControlRole.Admin, tokenVersion: 0),
                },
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);

        foreach (CreateChannelCommand command in invalid)
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
    public async Task CreateNormalizesWritesAndAtomicallyCompletesAuditEventAndIdempotency()
    {
        TestEnvironment environment = new();
        environment.Repository.CreateFactory = write => Written(
            new ChannelResource(
                write.ChannelId,
                write.Provider,
                write.Name,
                ChannelResourceStatus.Disabled,
                write.Capabilities,
                write.ModelMappings,
                Version: 1,
                CreatedAt: Now,
                UpdatedAt: Now));
        CreateChannelCommand command = CreateCommand() with
        {
            Actor = Actor(AccountControlRole.Operator),
            Name = "  Primary  ",
            ModelMappings =
            [
                new ChannelModelMappingView("model-z", "upstream-z"),
                new ChannelModelMappingView("model-a", "upstream-a"),
            ],
        };

        Result<SupplyCommandOutcome<ChannelView>> result =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(201, result.Value.StatusCode);
        Assert.False(result.Value.IsReplay);
        Assert.Equal("\"v1\"", result.Value.ETag);
        Assert.Equal(
            $"/api/v1/admin/channels/{result.Value.Value.Id.Value:D}",
            result.Value.Location);
        Assert.Equal("Primary", result.Value.Value.Name);
        Assert.Equal(
            ["model-a", "model-z"],
            result.Value.Value.ModelMappings.Select(static item => item.ClientModel));
        Assert.Equal(ChannelLifecycle.Disabled, result.Value.Value.Status);

        ChannelCreateWrite write = Assert.IsType<ChannelCreateWrite>(
            environment.Repository.LastCreate);
        Assert.Equal("Primary", write.Name);
        Assert.Equal(UpstreamProvider.OpenAiCompatible, write.Provider);
        Assert.True(write.Capabilities.Responses);
        Assert.Equal(
            ["model-a", "model-z"],
            write.ModelMappings.Select(static item => item.ClientModel));

        CommandIdempotencyRequest request =
            Assert.Single(environment.Idempotency.Requests);
        Assert.EndsWith(
            ":post:/api/v1/admin/channels",
            request.Scope,
            StringComparison.Ordinal);
        Assert.Equal($"user:{command.Actor.UserId.Value:D}", request.ActorFingerprint);
        Assert.Equal(command.RequestId, request.Owner);
        Assert.Equal(32, request.RequestHash.Length);
        Assert.Equal(TimeSpan.FromSeconds(30), request.LeaseDuration);
        Assert.Equal(TimeSpan.FromHours(24), request.Retention);

        AuditEntry audit = Assert.Single(environment.Audit.Entries);
        Assert.Equal(AuditActorType.Operator, audit.ActorType);
        Assert.Equal("supply.channel.created", audit.Action);
        Assert.Equal(result.Value.Value.Id, audit.TargetId);
        Assert.Null(audit.BeforeState);
        Assert.Equal("disabled", audit.AfterState!.Value.GetProperty("status").GetString());
        Assert.DoesNotContain(
            command.IdempotencyKey,
            audit.Metadata.GetRawText(),
            StringComparison.Ordinal);

        IntegrationEvent integrationEvent =
            Assert.Single(environment.Outbox.Events);
        Assert.Equal("poolai.supply.v1", integrationEvent.Topic);
        Assert.Equal("channel_created", integrationEvent.EventType);
        Assert.Equal(result.Value.Value.Id, integrationEvent.AggregateId);
        Assert.Equal(1, integrationEvent.AggregateVersion);
        Assert.Equal(Now, integrationEvent.OccurredAt);

        CommandIdempotencyCompletion completion =
            Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, completion.TerminalStatus);
        Assert.Equal(201, completion.ResponseStatus);
        Assert.Equal("channel", completion.ResourceType);
        Assert.Equal(result.Value.Value.Id, completion.ResourceId);
        Assert.Equal("\"v1\"", Header(completion, "ETag"));
        Assert.Equal(result.Value.Location, Header(completion, "Location"));
        Assert.Equal(1, environment.UnitOfWork.BeginCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        Assert.Equal(1, environment.UnitOfWork.DisposeCalls);
    }

    [Fact]
    public async Task MutationFailuresMapDispositionEtagAndDurableIdempotentFailure()
    {
        (ChannelMutationDisposition Disposition, string Code, int Status, string? ETag)[] cases =
        [
            (
                ChannelMutationDisposition.ValidationFailed,
                SupplyControlErrorCodes.ValidationFailed,
                422,
                null),
            (
                ChannelMutationDisposition.Conflict,
                SupplyControlErrorCodes.ResourceConflict,
                409,
                null),
            (
                ChannelMutationDisposition.NotFound,
                SupplyControlErrorCodes.ResourceNotFound,
                404,
                null),
            (
                ChannelMutationDisposition.VersionConflict,
                SupplyControlErrorCodes.VersionConflict,
                412,
                "\"v7\""),
            (
                ChannelMutationDisposition.LifecycleConflict,
                SupplyControlErrorCodes.ResourceConflict,
                409,
                null),
            (
                ChannelMutationDisposition.ChannelInUse,
                SupplyControlErrorCodes.ChannelInUse,
                409,
                null),
        ];

        foreach ((ChannelMutationDisposition disposition, string code, int status, string? etag)
                 in cases)
        {
            TestEnvironment environment = new();
            environment.Repository.UpdateResults.Enqueue(new ChannelMutationResult(
                disposition,
                WasChanged: false,
                Value: null,
                Before: null,
                CurrentVersion: 7));

            Result<SupplyCommandOutcome<ChannelView>> result =
                await environment.Service.ExecuteAsync(
                    UpdateCommand(EntityId.New()),
                    TestContext.Current.CancellationToken);

            AssertFailure(result, code);
            Assert.Equal(etag, result.Error.ETag);
            CommandIdempotencyCompletion completion =
                Assert.Single(environment.Idempotency.Completions);
            Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
            Assert.Equal(status, completion.ResponseStatus);
            Assert.Equal(code, completion.ResponseBody!.Value.GetProperty("Code").GetString());
            Assert.Equal(etag, Header(completion, "ETag"));
            Assert.Null(completion.ResourceType);
            Assert.Null(completion.ResourceId);
            Assert.Equal(1, environment.UnitOfWork.CommitCalls);
            Assert.Empty(environment.Audit.Entries);
            Assert.Empty(environment.Outbox.Events);
        }
    }

    [Fact]
    public async Task UpdateRejectsPresenceLifecycleAndReasonViolationsBeforeTransaction()
    {
        EntityId channelId = EntityId.New();
        UpdateChannelCommand valid = UpdateCommand(channelId);
        TestEnvironment environment = new();
        UpdateChannelCommand[] invalid =
        [
            valid with { Actor = Actor(AccountControlRole.User) },
            valid with { ExpectedVersion = 0 },
            valid with
            {
                NameSpecified = false,
                Name = null,
                StatusSpecified = false,
                Status = null,
            },
            valid with { NameSpecified = false, Name = "unexpected" },
            valid with { NameSpecified = true, Name = null },
            valid with
            {
                NameSpecified = false,
                Name = null,
                StatusSpecified = true,
                Status = null,
            },
            valid with
            {
                NameSpecified = false,
                Name = null,
                StatusSpecified = true,
                Status = ChannelLifecycle.Retired,
            },
            valid with
            {
                NameSpecified = true,
                StatusSpecified = false,
                Status = ChannelLifecycle.Active,
            },
            valid with
            {
                CapabilitiesSpecified = true,
                Capabilities = null,
            },
            valid with
            {
                CapabilitiesSpecified = false,
                Capabilities = new ChannelCapabilitiesSnapshot(true, true, true, true),
            },
            valid with
            {
                ModelMappingsSpecified = true,
                ModelMappings = null,
            },
            valid with
            {
                ModelMappingsSpecified = false,
                ModelMappings =
                [
                    new ChannelModelMappingView("unexpected", "unexpected"),
                ],
            },
            valid with { Reason = "invalid\nreason" },
            valid with
            {
                NameSpecified = false,
                Name = null,
                StatusSpecified = true,
                Status = ChannelLifecycle.Active,
                Reason = null,
            },
        ];

        foreach (UpdateChannelCommand command in invalid)
        {
            Result<SupplyCommandOutcome<ChannelView>> result =
                await environment.Service.ExecuteAsync(
                    command,
                    TestContext.Current.CancellationToken);
            AssertFailure(
                result,
                command.Actor.Role == AccountControlRole.User
                    ? SupplyControlErrorCodes.RoleRequired
                    : SupplyControlErrorCodes.ValidationFailed);
        }

        Assert.Equal(0, environment.UnitOfWork.BeginCalls);
        Assert.Equal(0, environment.Repository.UpdateCalls);
    }

    [Fact]
    public async Task UpdatePersistsNormalizedPatchAndEmitsOnlyMaterialChanges()
    {
        EntityId channelId = EntityId.New();
        ChannelResource before = Channel(channelId, "Before", version: 1);
        ChannelResource after = before with
        {
            Name = "After",
            Status = ChannelResourceStatus.Active,
            ModelMappings =
            [
                new ChannelModelMappingValue("model-a", "upstream-a"),
                new ChannelModelMappingValue("model-z", "upstream-z"),
            ],
            Version = 2,
            UpdatedAt = Now.AddMinutes(1),
        };
        TestEnvironment changed = new();
        changed.Repository.UpdateResults.Enqueue(Written(after, before));
        UpdateChannelCommand command = UpdateCommand(channelId) with
        {
            Name = "  After  ",
            StatusSpecified = true,
            Status = ChannelLifecycle.Active,
            ModelMappingsSpecified = true,
            ModelMappings =
            [
                new ChannelModelMappingView("model-z", "upstream-z"),
                new ChannelModelMappingView("model-a", "upstream-a"),
            ],
            Reason = "  activate for rollout  ",
        };

        Result<SupplyCommandOutcome<ChannelView>> result =
            await changed.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.Value.StatusCode);
        Assert.Equal("\"v2\"", result.Value.ETag);
        Assert.Null(result.Value.Location);
        ChannelUpdateWrite write =
            Assert.IsType<ChannelUpdateWrite>(changed.Repository.LastUpdate);
        Assert.Equal("After", write.Name);
        Assert.Equal(ChannelResourceStatus.Active, write.Status);
        Assert.Equal("activate for rollout", write.Reason);
        Assert.Equal(
            ["model-a", "model-z"],
            write.ModelMappings!.Select(static item => item.ClientModel));
        Assert.Equal("supply.channel.updated", Assert.Single(changed.Audit.Entries).Action);
        Assert.Equal("channel_updated", Assert.Single(changed.Outbox.Events).EventType);
        Assert.Equal(1, changed.UnitOfWork.CommitCalls);

        TestEnvironment unchanged = new();
        unchanged.Repository.UpdateResults.Enqueue(new ChannelMutationResult(
            ChannelMutationDisposition.Written,
            WasChanged: false,
            before,
            before,
            CurrentVersion: before.Version));
        Result<SupplyCommandOutcome<ChannelView>> noChange =
            await unchanged.Service.ExecuteAsync(
                UpdateCommand(channelId),
                TestContext.Current.CancellationToken);

        Assert.True(noChange.IsSuccess);
        Assert.Equal("\"v1\"", noChange.Value.ETag);
        Assert.Empty(unchanged.Audit.Entries);
        Assert.Empty(unchanged.Outbox.Events);
        Assert.Single(unchanged.Idempotency.Completions);
        Assert.Equal(1, unchanged.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task RetireEnforcesValidationAndMapsInUseAndSuccess()
    {
        EntityId channelId = EntityId.New();
        RetireChannelCommand valid = RetireCommand(channelId);
        TestEnvironment invalid = new();

        AssertFailure(
            await invalid.Service.ExecuteAsync(
                valid with { Actor = Actor(AccountControlRole.Auditor) },
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.RoleRequired);
        AssertFailure(
            await invalid.Service.ExecuteAsync(
                valid with { ExpectedVersion = 0 },
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.ValidationFailed);
        AssertFailure(
            await invalid.Service.ExecuteAsync(
                valid with { Reason = " " },
                TestContext.Current.CancellationToken),
            SupplyControlErrorCodes.ValidationFailed);
        Assert.Equal(0, invalid.UnitOfWork.BeginCalls);

        TestEnvironment blocked = new();
        blocked.Repository.RetireResults.Enqueue(new ChannelMutationResult(
            ChannelMutationDisposition.ChannelInUse,
            WasChanged: false,
            Value: null,
            Before: null,
            CurrentVersion: 3));
        Result<SupplyCommandOutcome> inUse = await blocked.Service.ExecuteAsync(
            valid,
            TestContext.Current.CancellationToken);

        AssertFailure(inUse, SupplyControlErrorCodes.ChannelInUse);
        Assert.Equal(1, blocked.UnitOfWork.CommitCalls);
        Assert.Empty(blocked.Audit.Entries);
        Assert.Empty(blocked.Outbox.Events);

        ChannelResource before = Channel(channelId, "Retiring", version: 3);
        ChannelResource retired = before with
        {
            Status = ChannelResourceStatus.Retired,
            Version = 4,
            UpdatedAt = Now.AddMinutes(2),
        };
        TestEnvironment success = new();
        success.Repository.RetireResults.Enqueue(Written(retired, before));
        Result<SupplyCommandOutcome> result = await success.Service.ExecuteAsync(
            valid with { ExpectedVersion = 3 },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(204, result.Value.StatusCode);
        Assert.False(result.Value.IsReplay);
        Assert.Equal("\"v4\"", result.Value.ETag);
        ChannelRetireWrite write =
            Assert.IsType<ChannelRetireWrite>(success.Repository.LastRetire);
        Assert.Equal(3, write.ExpectedVersion);
        Assert.Equal("decommissioned", write.Reason);
        Assert.Equal("supply.channel.retired", Assert.Single(success.Audit.Entries).Action);
        Assert.Equal("channel_retired", Assert.Single(success.Outbox.Events).EventType);
        CommandIdempotencyCompletion completion =
            Assert.Single(success.Idempotency.Completions);
        Assert.Equal(204, completion.ResponseStatus);
        Assert.Null(completion.ResponseBody);
        Assert.Equal(channelId, completion.ResourceId);
        Assert.Equal(1, success.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task IdempotencyReplayPrecedesEtagCheckAndEtagsAreResourceScoped()
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
            Result<SupplyCommandOutcome<ChannelView>> result =
                await environment.Service.ExecuteAsync(
                    CreateCommand(),
                    TestContext.Current.CancellationToken);

            AssertFailure(result, code);
            Assert.Equal(retry, result.Error.RetryAfterSeconds);
            Assert.Equal(0, environment.Repository.CreateCalls);
            Assert.Equal(0, environment.UnitOfWork.CommitCalls);
            Assert.Empty(environment.Idempotency.Completions);
        }

        ChannelResource channel = Channel(EntityId.New(), "Replay", version: 8);
        ChannelView view = View(channel);
        string replayLocation =
            $"/api/v1/admin/channels/{channel.Id.Value:D}";
        TestEnvironment replay = new();
        replay.Idempotency.AcquireResult = CommandIdempotencyAcquireResult.Replay(
            new CommandIdempotencyResponse(
                CommandIdempotencyTerminalStatus.Completed,
                Status: 201,
                Body: ChannelReplayBody(view),
                BodyEnvelope: null,
                Headers: Headers("\"v8\"", replayLocation),
                ResourceType: "channel",
                ResourceId: channel.Id));

        Result<SupplyCommandOutcome<ChannelView>> replayed =
            await replay.Service.ExecuteAsync(
                CreateCommand(),
                TestContext.Current.CancellationToken);

        Assert.True(replayed.IsSuccess);
        Assert.True(replayed.Value.IsReplay);
        Assert.Equal(view.Id, replayed.Value.Value.Id);
        Assert.Equal(view.Name, replayed.Value.Value.Name);
        Assert.Equal(view.Version, replayed.Value.Value.Version);
        Assert.Equal(
            view.ModelMappings.Select(static item => item.ClientModel),
            replayed.Value.Value.ModelMappings.Select(static item => item.ClientModel));
        Assert.Equal("\"v8\"", replayed.Value.ETag);
        Assert.Equal(replayLocation, replayed.Value.Location);
        Assert.Equal(0, replay.Repository.CreateCalls);
        Assert.Equal(0, replay.UnitOfWork.CommitCalls);

        TestEnvironment staleUpdateReplay = new();
        staleUpdateReplay.Idempotency.AcquireResult =
            CommandIdempotencyAcquireResult.Replay(
                new CommandIdempotencyResponse(
                    CommandIdempotencyTerminalStatus.Completed,
                    Status: 200,
                    Body: ChannelReplayBody(view),
                    BodyEnvelope: null,
                    Headers: Headers("\"v8\""),
                    ResourceType: "channel",
                    ResourceId: channel.Id));

        Result<SupplyCommandOutcome<ChannelView>> staleUpdate =
            await staleUpdateReplay.Service.ExecuteAsync(
                UpdateCommand(channel.Id) with { ExpectedVersion = 999 },
                TestContext.Current.CancellationToken);

        Assert.True(staleUpdate.IsSuccess);
        Assert.True(staleUpdate.Value.IsReplay);
        Assert.Equal(channel.Id, staleUpdate.Value.Value.Id);
        Assert.Equal("\"v8\"", staleUpdate.Value.ETag);
        Assert.Equal(0, staleUpdateReplay.Repository.UpdateCalls);
        Assert.Equal(0, staleUpdateReplay.UnitOfWork.CommitCalls);
    }

    private static CreateChannelCommand CreateCommand() => new(
        EntityId.New(),
        Actor(AccountControlRole.Admin),
        "channel-create-key",
        "Primary",
        UpstreamProvider.OpenAiCompatible,
        new ChannelCapabilitiesSnapshot(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true),
        [new ChannelModelMappingView("model-a", "upstream-a")],
        IpAddress: "127.0.0.1",
        UserAgent: "unit-test");

    private static UpdateChannelCommand UpdateCommand(EntityId channelId) => new(
        EntityId.New(),
        Actor(AccountControlRole.Operator),
        "channel-update-key",
        channelId,
        ExpectedVersion: 1,
        NameSpecified: true,
        Name: "Updated",
        StatusSpecified: false,
        Status: null,
        CapabilitiesSpecified: false,
        Capabilities: null,
        ModelMappingsSpecified: false,
        ModelMappings: null,
        Reason: "maintenance",
        IpAddress: null,
        UserAgent: null);

    private static RetireChannelCommand RetireCommand(EntityId channelId) => new(
        EntityId.New(),
        Actor(AccountControlRole.Admin),
        "channel-retire-key",
        channelId,
        ExpectedVersion: 1,
        Reason: "decommissioned",
        IpAddress: null,
        UserAgent: null);

    private static AccountActor Actor(
        AccountControlRole role,
        long tokenVersion = 1) => new(
        EntityId.New(),
        role,
        tokenVersion);

    private static ChannelResource Channel(
        EntityId id,
        string name,
        long version,
        DateTimeOffset? createdAt = null) => new(
        id,
        UpstreamProvider.OpenAiCompatible,
        name,
        ChannelResourceStatus.Disabled,
        new ChannelCapabilitiesValue(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: false,
            Streaming: true),
        [new ChannelModelMappingValue("model-a", "upstream-a")],
        version,
        createdAt ?? Now,
        createdAt ?? Now);

    private static ChannelView View(ChannelResource channel) => new(
        channel.Id,
        channel.Name,
        channel.Provider,
        ChannelLifecycle.Disabled,
        new ChannelCapabilitiesSnapshot(true, true, false, true),
        [new ChannelModelMappingView("model-a", "upstream-a")],
        channel.Version,
        channel.CreatedAt,
        channel.UpdatedAt);

    private static ChannelMutationResult Written(
        ChannelResource value,
        ChannelResource? before = null) => new(
        ChannelMutationDisposition.Written,
        WasChanged: true,
        value,
        before,
        value.Version);

    private static JsonElement Headers(string etag, string? location = null)
    {
        Dictionary<string, string> headers = new(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
        };
        if (location is not null)
        {
            headers["Location"] = location;
        }

        return JsonSerializer.SerializeToElement(headers);
    }

    private static JsonElement ChannelReplayBody(ChannelView value) =>
        JsonSerializer.SerializeToElement(new
        {
            Id = value.Id.Value,
            value.Name,
            Provider = "openai_compatible",
            Status = "disabled",
            value.Capabilities,
            ModelMappings = value.ModelMappings.Select(static mapping => new
            {
                mapping.ClientModel,
                mapping.UpstreamModel,
            }),
            value.Version,
            value.CreatedAt,
            value.UpdatedAt,
        });

    private static string? Header(
        CommandIdempotencyCompletion completion,
        string name) =>
        completion.ResponseHeaders.TryGetProperty(name, out JsonElement value)
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
            Service = new ChannelControlPlaneService(
                Repository,
                UnitOfWork,
                new GroupSupplyCommandCoordinator(
                    Idempotency,
                    Audit,
                    Outbox,
                    new AccountControlPlanePolicy(
                        Enumerable.Range(1, 32)
                            .Select(static value => (byte)value)
                            .ToArray())));
        }

        internal FakeChannelRepository Repository { get; } = new();

        internal RecordingUnitOfWorkFactory UnitOfWork { get; } = new();

        internal RecordingIdempotencyStore Idempotency { get; } = new();

        internal RecordingAuditAppender Audit { get; } = new();

        internal RecordingOutboxAppender Outbox { get; } = new();

        internal ChannelControlPlaneService Service { get; }
    }

    private sealed class FakeChannelRepository : IChannelControlPlaneRepository
    {
        internal ChannelSlice ListResult { get; set; } = new([], HasMore: false);

        internal int ListCalls { get; private set; }

        internal ChannelCursor? LastCursor { get; private set; }

        internal ChannelResource? GetResult { get; set; }

        internal int CreateCalls { get; private set; }

        internal ChannelCreateWrite? LastCreate { get; private set; }

        internal ChannelUpdateWrite? LastUpdate { get; private set; }

        internal ChannelRetireWrite? LastRetire { get; private set; }

        internal Func<ChannelCreateWrite, ChannelMutationResult>? CreateFactory
        {
            get;
            set;
        }

        internal Queue<ChannelMutationResult> UpdateResults { get; } = [];

        internal Queue<ChannelMutationResult> RetireResults { get; } = [];

        internal int UpdateCalls { get; private set; }

        public ValueTask<ChannelSlice> ListAsync(
            ChannelCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            LastCursor = cursor;
            return ValueTask.FromResult(ListResult);
        }

        public ValueTask<ChannelResource?> GetAsync(
            EntityId channelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GetResult);
        }

        public ValueTask<ChannelMutationResult> CreateAsync(
            ChannelCreateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            LastCreate = write;
            return ValueTask.FromResult(
                CreateFactory?.Invoke(write)
                ?? new ChannelMutationResult(
                    ChannelMutationDisposition.Conflict,
                    WasChanged: false,
                    Value: null,
                    Before: null));
        }

        public ValueTask<ChannelMutationResult> UpdateAsync(
            ChannelUpdateWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCalls++;
            LastUpdate = write;
            return ValueTask.FromResult(UpdateResults.Dequeue());
        }

        public ValueTask<ChannelMutationResult> RetireAsync(
            ChannelRetireWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRetire = write;
            return ValueTask.FromResult(RetireResults.Dequeue());
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

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        internal CommandIdempotencyAcquireResult? AcquireResult { get; set; }

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
}
#pragma warning restore MA0051
