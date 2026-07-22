using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Application;
using PoolAI.Modules.SubscriptionAccess.Application.Ports;
using PoolAI.Modules.SubscriptionAccess.Domain;

namespace PoolAI.UnitTests;

public sealed class SubscriptionAccessUseCaseTests
{
    private static readonly DateTimeOffset CreatedAt = new(
        2026,
        7,
        17,
        1,
        0,
        0,
        TimeSpan.Zero);

    private static readonly DateTimeOffset UpdatedAt = CreatedAt.AddHours(1);

    [Fact]
    public async Task TemplateNoOpUsesWasChangedAndDoesNotAppendAuditOrOutbox()
    {
        SubscriptionTemplateRecord template = Template(version: 7);
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: false,
                template,
                JsonSerializer.SerializeToElement(new { name = "stale-before-state" }),
                CurrentVersion: 7),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(
                TemplateUpdateCommand(template),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v7\"", result.Value.ETag);
        Assert.Empty(environment.Audit.Entries);
        Assert.Empty(environment.Outbox.Events);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, completion.TerminalStatus);
        Assert.Equal(200, completion.ResponseStatus);
    }

    [Fact]
    public async Task SubscriptionNoOpUsesWasChangedAndDoesNotAppendAuditOrOutbox()
    {
        SubscriptionRecord subscription = Subscription(version: 11);
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionUpdateResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: false,
                subscription,
                JsonSerializer.SerializeToElement(new { status = "active" }),
                CurrentVersion: 11),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionUpdateCommand(subscription),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v11\"", result.Value.ETag);
        Assert.Empty(environment.Audit.Entries);
        Assert.Empty(environment.Outbox.Events);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, completion.TerminalStatus);
        Assert.Equal(200, completion.ResponseStatus);
    }

    [Fact]
    public async Task ChangedSubscriptionAppendsOneAuditAndEventAtMutationTimestamp()
    {
        SubscriptionRecord subscription = Subscription(version: 12);
        JsonElement beforeState = JsonSerializer.SerializeToElement(new
        {
            status = "active",
            version = 11,
        });
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionUpdateResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                subscription,
                beforeState,
                CurrentVersion: 12),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionUpdateCommand(subscription),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        AuditEntry audit = Assert.Single(environment.Audit.Entries);
        Assert.Equal("subscription_access.subscription.updated", audit.Action);
        Assert.Equal(beforeState, audit.BeforeState);
        IntegrationEvent integrationEvent = Assert.Single(environment.Outbox.Events);
        Assert.Equal("subscription_updated", integrationEvent.EventType);
        Assert.Equal(subscription.UpdatedAt, integrationEvent.OccurredAt);
        Assert.NotEqual(subscription.ObservedAt, integrationEvent.OccurredAt);
    }

    [Fact]
    public async Task TemplateNameConflictIsCompletedAsDurableResourceConflict()
    {
        SubscriptionTemplateRecord template = Template(version: 3);
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.ResourceConflict,
                WasChanged: false,
                Value: null,
                JsonSerializer.SerializeToElement(new { name = template.Name }),
                CurrentVersion: 3),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(
                TemplateUpdateCommand(template) with { Name = "Duplicate name" },
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceConflict, result.Error.Code);
        Assert.Equal(409, result.Error.Presentation!.Status);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(409, completion.ResponseStatus);
        Assert.Empty(environment.Audit.Entries);
        Assert.Empty(environment.Outbox.Events);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task OperatorRevokedRegrantIsCompletedAsDurableRoleFailure()
    {
        SubscriptionRecord subscription = Subscription(version: 5) with
        {
            Status = SubscriptionLifecycle.Revoked,
            EffectiveStatus = SubscriptionEffectiveLifecycle.Revoked,
        };
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionUpdateResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.InvalidTransition,
                WasChanged: false,
                Value: null,
                JsonSerializer.SerializeToElement(new { status = "revoked", version = 5 }),
                CurrentVersion: 5),
        };
        TestCommandEnvironment environment = new(repository);
        UpdateSubscriptionCommand command = SubscriptionUpdateCommand(subscription) with
        {
            Actor = new SubscriptionActor(EntityId.New(), SystemRole.Operator, 1),
            StartsAtSpecified = true,
            StartsAt = UpdatedAt.AddDays(1),
            ExpiresAtSpecified = true,
            ExpiresAt = UpdatedAt.AddDays(31),
            StatusSpecified = true,
            Status = SubscriptionLifecycle.Active,
        };

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.RoleRequired, result.Error.Code);
        Assert.Equal(403, result.Error.Presentation!.Status);
        Assert.False(repository.LastAllowRevokedRegrant);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(403, completion.ResponseStatus);
        Assert.Empty(environment.Audit.Entries);
        Assert.Empty(environment.Outbox.Events);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("disabled")]
    public async Task AssignMissingDisabledOrDeletedTargetUserIsDurableResourceConflict(
        string targetState)
    {
        EntityId userId = EntityId.New();
        Result<UserStatusSnapshot> userStatus = string.Equals(
            targetState,
            "disabled",
            StringComparison.Ordinal)
            ? Result.Success(new UserStatusSnapshot(
                userId,
                UserLifecycle.Disabled,
                SystemRole.User,
                3,
                7,
                UpdatedAt))
            : Result.Failure<UserStatusSnapshot>(
                SubscriptionErrorCodes.ResourceNotFound,
                $"The {targetState} target user is not visible.");
        RecordingUserStatusReader users = new(userStatus);
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository, users);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionAssignCommand(userId, EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceConflict, result.Error.Code);
        Assert.Equal(409, result.Error.Presentation!.Status);
        Assert.Equal(1, users.Calls);
        Assert.Equal(0, repository.SubscriptionAssignCalls);
        Assert.Empty(environment.Audit.Entries);
        Assert.Empty(environment.Outbox.Events);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(409, completion.ResponseStatus);
    }

    [Fact]
    public async Task CompletedAssignReplayDoesNotReadCurrentTargetUserAgain()
    {
        EntityId userId = EntityId.New();
        EntityId templateId = EntityId.New();
        SubscriptionRecord subscription = Subscription(version: 1) with
        {
            UserId = userId,
            TemplateId = templateId,
        };
        RecordingUserStatusReader users = new(Result.Success(new UserStatusSnapshot(
            userId,
            UserLifecycle.Active,
            SystemRole.User,
            3,
            7,
            UpdatedAt)));
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionAssignResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                subscription,
                BeforeState: null,
                CurrentVersion: 1),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            ReplayCompletedRequests = true,
        };
        TestCommandEnvironment environment = new(repository, users, idempotency);
        AssignSubscriptionCommand command = SubscriptionAssignCommand(userId, templateId);

        Result<SubscriptionCommandOutcome<SubscriptionView>> initial =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);
        users.CurrentResult = Result.Success(new UserStatusSnapshot(
            userId,
            UserLifecycle.Disabled,
            SystemRole.User,
            4,
            8,
            UpdatedAt.AddMinutes(1)));
        Result<SubscriptionCommandOutcome<SubscriptionView>> replay =
            await environment.Service.ExecuteAsync(
                command,
                TestContext.Current.CancellationToken);

        Assert.True(initial.IsSuccess);
        Assert.False(initial.Value.IsReplay);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(subscription.Id, replay.Value.Value.Id);
        Assert.Equal(1, users.Calls);
        Assert.Equal(1, repository.SubscriptionAssignCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        Assert.Single(environment.Idempotency.Completions);
    }

    [Fact]
    public async Task CreateTemplateMissingGroupIsDurablePostResourceConflict()
    {
        FakeSubscriptionRepository repository = new()
        {
            TemplateCreateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.NotFound,
                WasChanged: false,
                Value: null,
                BeforeState: null),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(
                TemplateCreateCommand(EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceConflict, result.Error.Code);
        Assert.Equal(409, result.Error.Presentation!.Status);
        Assert.Equal(1, repository.TemplateCreateCalls);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(409, completion.ResponseStatus);
    }

    [Fact]
    public async Task AssignMissingTemplateIsDurablePostResourceConflict()
    {
        EntityId userId = EntityId.New();
        RecordingUserStatusReader users = new(Result.Success(new UserStatusSnapshot(
            userId,
            UserLifecycle.Active,
            SystemRole.User,
            3,
            7,
            UpdatedAt)));
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionAssignResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.NotFound,
                WasChanged: false,
                Value: null,
                BeforeState: null),
        };
        TestCommandEnvironment environment = new(repository, users);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionAssignCommand(userId, EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceConflict, result.Error.Code);
        Assert.Equal(409, result.Error.Presentation!.Status);
        Assert.Equal(1, users.Calls);
        Assert.Equal(1, repository.SubscriptionAssignCalls);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(409, completion.ResponseStatus);
    }

    [Fact]
    public async Task TemplateListReturnsViewsAndRoundTripsCanonicalCursor()
    {
        SubscriptionTemplateRecord first = Template(version: 2) with
        {
            CreatedAt = CreatedAt,
        };
        SubscriptionTemplateRecord last = Template(version: 3) with
        {
            CreatedAt = CreatedAt.AddMinutes(1),
        };
        FakeSubscriptionRepository repository = new()
        {
            TemplateListResult = new SubscriptionTemplateSlice([first, last], HasMore: true),
        };
        TestCommandEnvironment environment = new(repository);
        SubscriptionActor actor = Actor(SystemRole.Auditor);

        Result<SubscriptionTemplatePage> firstPage = await environment.Service.ExecuteAsync(
            new ListSubscriptionTemplatesQuery(actor, Cursor: null, Limit: 2),
            TestContext.Current.CancellationToken);

        Assert.True(firstPage.IsSuccess);
        Assert.Equal([first.Id, last.Id], firstPage.Value.Data.Select(static item => item.Id));
        Assert.True(firstPage.Value.HasMore);
        Assert.NotNull(firstPage.Value.NextCursor);

        Result<SubscriptionTemplatePage> secondPage = await environment.Service.ExecuteAsync(
            new ListSubscriptionTemplatesQuery(actor, firstPage.Value.NextCursor, Limit: 2),
            TestContext.Current.CancellationToken);

        Assert.True(secondPage.IsSuccess);
        Assert.Equal(2, repository.TemplateListCalls);
        Assert.Equal(last.Id, repository.LastTemplateCursor!.Id);
        Assert.Equal(last.CreatedAt, repository.LastTemplateCursor.CreatedAt);
    }

    [Theory]
    [InlineData(false, null, 50, SubscriptionErrorCodes.RoleRequired)]
    [InlineData(true, null, 0, SubscriptionErrorCodes.InvalidRequest)]
    [InlineData(true, null, 101, SubscriptionErrorCodes.InvalidRequest)]
    [InlineData(true, "bad", 50, SubscriptionErrorCodes.InvalidRequest)]
    [InlineData(true, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 50, SubscriptionErrorCodes.InvalidRequest)]
    public async Task TemplateListRejectsUnauthorizedOrInvalidPagination(
        bool authorized,
        string? cursor,
        int limit,
        string expectedCode)
    {
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);
        SubscriptionActor actor = authorized
            ? Actor(SystemRole.Admin)
            : Actor(SystemRole.User);

        Result<SubscriptionTemplatePage> result = await environment.Service.ExecuteAsync(
            new ListSubscriptionTemplatesQuery(actor, cursor, limit),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(0, repository.TemplateListCalls);
    }

    [Fact]
    public async Task GetTemplateMapsFoundMissingAndUnauthorizedResults()
    {
        SubscriptionTemplateRecord template = Template(version: 4);
        FakeSubscriptionRepository repository = new()
        {
            TemplateGetResult = template,
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionTemplateView> found = await environment.Service.ExecuteAsync(
            new GetSubscriptionTemplateQuery(Actor(SystemRole.Operator), template.Id),
            TestContext.Current.CancellationToken);
        repository.TemplateGetResult = null;
        Result<SubscriptionTemplateView> missing = await environment.Service.ExecuteAsync(
            new GetSubscriptionTemplateQuery(Actor(SystemRole.Auditor), template.Id),
            TestContext.Current.CancellationToken);
        Result<SubscriptionTemplateView> forbidden = await environment.Service.ExecuteAsync(
            new GetSubscriptionTemplateQuery(Actor(SystemRole.User), template.Id),
            TestContext.Current.CancellationToken);

        Assert.True(found.IsSuccess);
        Assert.Equal(template.Id, found.Value.Id);
        Assert.True(missing.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceNotFound, missing.Error.Code);
        Assert.True(forbidden.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.RoleRequired, forbidden.Error.Code);
        Assert.Equal(2, repository.TemplateGetCalls);
    }

    [Fact]
    public async Task SubscriptionListsCoverSelfAndAdminCursorPaths()
    {
        EntityId userId = EntityId.New();
        SubscriptionRecord first = Subscription(version: 1) with
        {
            UserId = userId,
            CreatedAt = CreatedAt,
        };
        SubscriptionRecord last = Subscription(version: 2) with
        {
            UserId = userId,
            CreatedAt = CreatedAt.AddMinutes(1),
        };
        FakeSubscriptionRepository repository = new()
        {
            UserSubscriptionsResult = [first, last],
            SubscriptionListResult = new SubscriptionSlice([first, last], HasMore: true),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionPage> own = await environment.Service.ExecuteAsync(
            new ListSubscriptionsQuery(
                new SubscriptionActor(userId, SystemRole.User, 2),
                Cursor: "ignored-for-self",
                Limit: 0,
                UserId: null,
                GroupId: null,
                IsSelfQuery: true),
            TestContext.Current.CancellationToken);
        Result<SubscriptionPage> admin = await environment.Service.ExecuteAsync(
            new ListSubscriptionsQuery(
                Actor(SystemRole.Admin),
                Cursor: null,
                Limit: 2,
                UserId: userId,
                GroupId: first.GroupId,
                IsSelfQuery: false),
            TestContext.Current.CancellationToken);
        Result<SubscriptionPage> next = await environment.Service.ExecuteAsync(
            new ListSubscriptionsQuery(
                Actor(SystemRole.Operator),
                admin.Value.NextCursor,
                Limit: 2,
                UserId: userId,
                GroupId: first.GroupId,
                IsSelfQuery: false),
            TestContext.Current.CancellationToken);

        Assert.True(own.IsSuccess);
        Assert.False(own.Value.HasMore);
        Assert.Null(own.Value.NextCursor);
        Assert.Equal(2, own.Value.Data.Count);
        Assert.True(admin.IsSuccess);
        Assert.True(admin.Value.HasMore);
        Assert.NotNull(admin.Value.NextCursor);
        Assert.True(next.IsSuccess);
        Assert.Equal(1, repository.UserSubscriptionsCalls);
        Assert.Equal(2, repository.SubscriptionListCalls);
        Assert.Equal(last.Id, repository.LastSubscriptionCursor!.Id);
        Assert.Equal(userId, repository.LastSubscriptionUserId);
        Assert.Equal(first.GroupId, repository.LastSubscriptionGroupId);
    }

    [Fact]
    public async Task SubscriptionListRejectsInvalidSelfAdminRoleAndPagination()
    {
        EntityId actorId = EntityId.New();
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);
        ListSubscriptionsQuery[] queries =
        [
            new(new SubscriptionActor(actorId, SystemRole.User, 0), null, 50, null, null, true),
            new(new SubscriptionActor(actorId, SystemRole.User, 1), null, 50, EntityId.New(), null, true),
            new(new SubscriptionActor(actorId, SystemRole.User, 1), null, 50, null, EntityId.New(), true),
            new(new SubscriptionActor(actorId, SystemRole.User, 1), null, 50, null, null, false),
            new(Actor(SystemRole.Admin), "bad", 50, null, null, false),
        ];

        foreach (ListSubscriptionsQuery query in queries)
        {
            Result<SubscriptionPage> result = await environment.Service.ExecuteAsync(
                query,
                TestContext.Current.CancellationToken);
            Assert.True(result.IsFailure);
        }

        Assert.Equal(0, repository.UserSubscriptionsCalls);
        Assert.Equal(0, repository.SubscriptionListCalls);
    }

    [Fact]
    public async Task GetSubscriptionMapsFoundMissingAndUnauthorizedResults()
    {
        SubscriptionRecord subscription = Subscription(version: 6);
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionGetResult = subscription,
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionView> found = await environment.Service.ExecuteAsync(
            new GetSubscriptionQuery(Actor(SystemRole.Auditor), subscription.Id),
            TestContext.Current.CancellationToken);
        repository.SubscriptionGetResult = null;
        Result<SubscriptionView> missing = await environment.Service.ExecuteAsync(
            new GetSubscriptionQuery(Actor(SystemRole.Admin), subscription.Id),
            TestContext.Current.CancellationToken);
        Result<SubscriptionView> forbidden = await environment.Service.ExecuteAsync(
            new GetSubscriptionQuery(Actor(SystemRole.User), subscription.Id),
            TestContext.Current.CancellationToken);

        Assert.True(found.IsSuccess);
        Assert.Equal(subscription.Id, found.Value.Id);
        Assert.True(missing.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ResourceNotFound, missing.Error.Code);
        Assert.True(forbidden.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.RoleRequired, forbidden.Error.Code);
        Assert.Equal(2, repository.SubscriptionGetCalls);
        Assert.Null(repository.LastVisibleToUserId);
    }

    [Fact]
    public async Task CreateTemplateSuccessAppendsFactsAndReplaysStoredResponse()
    {
        SubscriptionTemplateRecord template = Template(version: 1);
        FakeSubscriptionRepository repository = new()
        {
            TemplateCreateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                template,
                BeforeState: null,
                CurrentVersion: 1),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            ReplayCompletedRequests = true,
        };
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);
        CreateSubscriptionTemplateCommand command = TemplateCreateCommand(template.GroupId) with
        {
            Name = "  Stable template  ",
        };

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> initial =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);
        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> replay =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(initial.IsSuccess);
        Assert.Equal(201, initial.Value.StatusCode);
        Assert.False(initial.Value.IsReplay);
        Assert.Equal("Stable template", repository.LastTemplateCreateName);
        Assert.Equal($"/api/v1/admin/subscription-templates/{template.Id.Value:D}", initial.Value.Location);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(template.Id, replay.Value.Value.Id);
        Assert.Equal(1, repository.TemplateCreateCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        Assert.Equal("subscription_access.template.created", Assert.Single(environment.Audit.Entries).Action);
        Assert.Equal("template_created", Assert.Single(environment.Outbox.Events).EventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task CreateTemplateRejectsInvalidRoleOrInput(int scenario)
    {
        CreateSubscriptionTemplateCommand command = TemplateCreateCommand(EntityId.New());
        command = scenario switch
        {
            0 => command with { Actor = Actor(SystemRole.User) },
            1 => command with { IdempotencyKey = string.Empty },
            2 => command with { Name = " \t " },
            3 => command with { Name = new string('n', 101) },
            4 => command with { Description = new string('d', 1001) },
            5 => command with { DefaultDurationDays = 0 },
            _ => command with { DefaultDurationDays = 3651 },
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(
            scenario == 0 ? SubscriptionErrorCodes.RoleRequired : SubscriptionErrorCodes.ValidationFailed,
            result.Error.Code);
        Assert.Equal(0, repository.TemplateCreateCalls);
        Assert.Equal(0, environment.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task ChangedTemplateUpdateAppendsAuditAndEvent()
    {
        SubscriptionTemplateRecord template = Template(version: 8) with
        {
            Name = "Renamed",
            Description = null,
            DefaultDurationDays = 90,
            Status = SubscriptionTemplateLifecycle.Disabled,
        };
        JsonElement beforeState = JsonSerializer.SerializeToElement(new { version = 7 });
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                template,
                beforeState,
                CurrentVersion: 8),
        };
        TestCommandEnvironment environment = new(repository);
        UpdateSubscriptionTemplateCommand command = TemplateUpdateCommand(template) with
        {
            ExpectedVersion = 7,
            Name = " Renamed ",
            DescriptionSpecified = true,
            Description = null,
            DefaultDurationDaysSpecified = true,
            DefaultDurationDays = 90,
            StatusSpecified = true,
            Status = SubscriptionTemplateLifecycle.Disabled,
            Reason = " pause grants ",
        };

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("\"v8\"", result.Value.ETag);
        Assert.Equal("Renamed", repository.LastTemplateUpdateName);
        Assert.Equal("pause grants", repository.LastTemplateUpdateReason);
        Assert.False(repository.LastTemplateRetire);
        AuditEntry audit = Assert.Single(environment.Audit.Entries);
        Assert.Equal("subscription_access.template.updated", audit.Action);
        Assert.Equal(beforeState, audit.BeforeState);
        Assert.Equal("template_updated", Assert.Single(environment.Outbox.Events).EventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task UpdateTemplateRejectsInvalidInput(int scenario)
    {
        SubscriptionTemplateRecord template = Template(version: 3);
        UpdateSubscriptionTemplateCommand command = TemplateUpdateCommand(template);
        command = scenario switch
        {
            0 => command with { IdempotencyKey = string.Empty },
            1 => command with { ExpectedVersion = 0 },
            2 => command with { NameSpecified = false, Name = null },
            3 => command with { Name = "\u0001" },
            4 => command with { DescriptionSpecified = true, Description = "\u0001" },
            5 => command with { DefaultDurationDaysSpecified = true, DefaultDurationDays = 0 },
            6 => command with
            {
                StatusSpecified = true,
                Status = SubscriptionTemplateLifecycle.Retired,
                Reason = "retire",
            },
            7 => command with
            {
                StatusSpecified = true,
                Status = SubscriptionTemplateLifecycle.Disabled,
                Reason = null,
            },
            _ => command with { Reason = "\u0001" },
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ValidationFailed, result.Error.Code);
        Assert.Equal(0, repository.TemplateUpdateCalls);
    }

    [Theory]
    [InlineData(1, SubscriptionErrorCodes.ResourceNotFound, 404, null)]
    [InlineData(2, SubscriptionErrorCodes.VersionConflict, 412, "\"v9\"")]
    [InlineData(6, SubscriptionErrorCodes.SubscriptionTemplateDisabled, 409, null)]
    [InlineData(7, SubscriptionErrorCodes.SubscriptionConflict, 409, null)]
    [InlineData(4, SubscriptionErrorCodes.ResourceConflict, 409, null)]
    [InlineData(5, SubscriptionErrorCodes.GroupDisabled, 403, null)]
    [InlineData(8, SubscriptionErrorCodes.ResourceConflict, 409, null)]
    public async Task TemplateMutationDispositionsMapToDurableFailures(
        int dispositionValue,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        SubscriptionTemplateRecord template = Template(version: 3);
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                (SubscriptionMutationDisposition)dispositionValue,
                WasChanged: false,
                Value: null,
                BeforeState: null,
                CurrentVersion: 9),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(
                TemplateUpdateCommand(template),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedStatus, result.Error.Presentation!.Status);
        Assert.Equal(expectedEtag, result.Error.ETag);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
        Assert.Equal(expectedStatus, Assert.Single(environment.Idempotency.Completions).ResponseStatus);
    }

    [Fact]
    public async Task RetireTemplateSuccessAppendsFactsAndReplaysWithoutAnotherMutation()
    {
        SubscriptionTemplateRecord template = Template(version: 10) with
        {
            Status = SubscriptionTemplateLifecycle.Retired,
        };
        JsonElement beforeState = JsonSerializer.SerializeToElement(new { version = 9 });
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                template,
                beforeState,
                CurrentVersion: 10),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            ReplayCompletedRequests = true,
        };
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);
        RetireSubscriptionTemplateCommand command = TemplateRetireCommand(template.Id, 9);

        Result<SubscriptionCommandOutcome> initial = await environment.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);
        Result<SubscriptionCommandOutcome> replay = await environment.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(initial.IsSuccess);
        Assert.Equal(204, initial.Value.StatusCode);
        Assert.False(initial.Value.IsReplay);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal("\"v10\"", replay.Value.ETag);
        Assert.Equal(1, repository.TemplateUpdateCalls);
        Assert.True(repository.LastTemplateRetire);
        Assert.Equal("retire template", repository.LastTemplateUpdateReason);
        Assert.Equal("subscription_access.template.retired", Assert.Single(environment.Audit.Entries).Action);
        Assert.Equal("template_retired", Assert.Single(environment.Outbox.Events).EventType);
        CommandIdempotencyCompletion completion = Assert.Single(environment.Idempotency.Completions);
        Assert.Equal(204, completion.ResponseStatus);
        Assert.Null(completion.ResponseBody);
    }

    [Fact]
    public async Task FailedRetireReplaysOriginalVersionConflict()
    {
        EntityId templateId = EntityId.New();
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.VersionConflict,
                WasChanged: false,
                Value: null,
                BeforeState: null,
                CurrentVersion: 12),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            ReplayCompletedRequests = true,
        };
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);
        RetireSubscriptionTemplateCommand command = TemplateRetireCommand(templateId, 11);

        Result<SubscriptionCommandOutcome> initial = await environment.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);
        Result<SubscriptionCommandOutcome> replay = await environment.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(initial.IsFailure);
        Assert.True(replay.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.VersionConflict, replay.Error.Code);
        Assert.Equal("\"v12\"", replay.Error.ETag);
        Assert.Equal(1, repository.TemplateUpdateCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RetireTemplateRejectsInvalidRoleOrInput(int scenario)
    {
        RetireSubscriptionTemplateCommand command = TemplateRetireCommand(EntityId.New(), 3);
        command = scenario switch
        {
            0 => command with { Actor = Actor(SystemRole.Auditor) },
            1 => command with { IdempotencyKey = string.Empty },
            2 => command with { ExpectedVersion = 0 },
            _ => command with { Reason = "  " },
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome> result = await environment.Service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(
            scenario == 0 ? SubscriptionErrorCodes.RoleRequired : SubscriptionErrorCodes.ValidationFailed,
            result.Error.Code);
        Assert.Equal(0, repository.TemplateUpdateCalls);
    }

    [Fact]
    public async Task AssignSubscriptionSuccessAppendsAuditEventAndLocation()
    {
        EntityId userId = EntityId.New();
        EntityId templateId = EntityId.New();
        SubscriptionRecord subscription = Subscription(version: 1) with
        {
            UserId = userId,
            TemplateId = templateId,
        };
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionAssignResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                subscription,
                BeforeState: null,
                CurrentVersion: 1),
        };
        RecordingUserStatusReader users = ActiveUser(userId);
        TestCommandEnvironment environment = new(repository, users);
        AssignSubscriptionCommand command = SubscriptionAssignCommand(userId, templateId) with
        {
            Reason = "  grant access  ",
        };

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(201, result.Value.StatusCode);
        Assert.Equal($"/api/v1/admin/subscriptions/{subscription.Id.Value:D}", result.Value.Location);
        Assert.Equal("grant access", repository.LastSubscriptionReason);
        Assert.Equal(1, users.Calls);
        Assert.Equal("subscription_access.subscription.assigned", Assert.Single(environment.Audit.Entries).Action);
        Assert.Equal("subscription_assigned", Assert.Single(environment.Outbox.Events).EventType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AssignSubscriptionRejectsInvalidRoleOrInput(int scenario)
    {
        AssignSubscriptionCommand command = SubscriptionAssignCommand(EntityId.New(), EntityId.New());
        command = scenario switch
        {
            0 => command with { Actor = Actor(SystemRole.User) },
            1 => command with { IdempotencyKey = string.Empty },
            2 => command with { Reason = "\u0001" },
            3 => command with { StartsAt = CreatedAt, ExpiresAt = CreatedAt },
            _ => command with { StartsAt = CreatedAt.AddDays(1), ExpiresAt = CreatedAt },
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(
            scenario == 0 ? SubscriptionErrorCodes.RoleRequired : SubscriptionErrorCodes.ValidationFailed,
            result.Error.Code);
        Assert.Equal(0, repository.SubscriptionAssignCalls);
        Assert.Equal(0, environment.Users.Calls);
    }

    [Fact]
    public async Task AssignForwardsNonVisibilityDependencyFailureWithoutDurableMutation()
    {
        EntityId userId = EntityId.New();
        RecordingUserStatusReader users = new(Result.Failure<UserStatusSnapshot>(
            "dependency_unavailable",
            "Identity is unavailable."));
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository, users);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionAssignCommand(userId, EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("dependency_unavailable", result.Error.Code);
        Assert.Equal(1, users.Calls);
        Assert.Equal(0, repository.SubscriptionAssignCalls);
        Assert.Empty(environment.Idempotency.Completions);
        Assert.Equal(0, environment.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(6, SubscriptionErrorCodes.SubscriptionTemplateDisabled, 409)]
    [InlineData(7, SubscriptionErrorCodes.SubscriptionConflict, 409)]
    [InlineData(4, SubscriptionErrorCodes.ResourceConflict, 409)]
    [InlineData(5, SubscriptionErrorCodes.GroupDisabled, 403)]
    [InlineData(3, SubscriptionErrorCodes.ResourceConflict, 409)]
    public async Task AssignMutationDispositionsMapToDurableFailures(
        int dispositionValue,
        string expectedCode,
        int expectedStatus)
    {
        EntityId userId = EntityId.New();
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionAssignResult = new SubscriptionMutationResult(
                (SubscriptionMutationDisposition)dispositionValue,
                WasChanged: false,
                Value: null,
                BeforeState: null),
        };
        TestCommandEnvironment environment = new(repository, ActiveUser(userId));

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionAssignCommand(userId, EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedStatus, result.Error.Presentation!.Status);
        Assert.Equal(1, repository.SubscriptionAssignCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task UpdateSubscriptionRejectsInvalidInput(int scenario)
    {
        SubscriptionRecord subscription = Subscription(version: 5);
        UpdateSubscriptionCommand command = SubscriptionUpdateCommand(subscription);
        command = scenario switch
        {
            0 => command with { IdempotencyKey = string.Empty },
            1 => command with { ExpectedVersion = 0 },
            2 => command with { StartsAtSpecified = false, ExpiresAtSpecified = false, StatusSpecified = false },
            3 => command with { StartsAtSpecified = true, StartsAt = null },
            4 => command with { ExpiresAtSpecified = true, ExpiresAt = null },
            5 => command with { StatusSpecified = true, Status = null },
            6 => command with
            {
                StartsAtSpecified = true,
                StartsAt = CreatedAt,
                ExpiresAtSpecified = true,
                ExpiresAt = CreatedAt,
            },
            7 => command with { Status = (SubscriptionLifecycle)999 },
            _ => command with { Reason = "\u0001" },
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.ValidationFailed, result.Error.Code);
        Assert.Equal(0, repository.SubscriptionUpdateCalls);
    }

    [Fact]
    public async Task AdminCanRequestRevokedSubscriptionRegrant()
    {
        SubscriptionRecord subscription = Subscription(version: 6);
        JsonElement before = JsonSerializer.SerializeToElement(new { status = "revoked" });
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionUpdateResult = new SubscriptionMutationResult(
                SubscriptionMutationDisposition.Updated,
                WasChanged: true,
                subscription,
                before,
                CurrentVersion: 6),
        };
        TestCommandEnvironment environment = new(repository);
        UpdateSubscriptionCommand command = SubscriptionUpdateCommand(subscription) with
        {
            ExpectedVersion = 5,
            StartsAtSpecified = true,
            StartsAt = CreatedAt.AddDays(1),
            ExpiresAtSpecified = true,
            ExpiresAt = CreatedAt.AddDays(31),
            Status = SubscriptionLifecycle.Active,
            Reason = "regrant",
        };

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(repository.LastAllowRevokedRegrant);
        Assert.Equal(1, repository.SubscriptionUpdateCalls);
        Assert.Single(environment.Audit.Entries);
        Assert.Single(environment.Outbox.Events);
    }

    [Theory]
    [InlineData(1, SubscriptionErrorCodes.ResourceNotFound, 404, null)]
    [InlineData(2, SubscriptionErrorCodes.VersionConflict, 412, "\"v8\"")]
    [InlineData(4, SubscriptionErrorCodes.ResourceConflict, 409, null)]
    [InlineData(5, SubscriptionErrorCodes.GroupDisabled, 403, null)]
    [InlineData(8, SubscriptionErrorCodes.ResourceConflict, 409, null)]
    public async Task SubscriptionMutationDispositionsMapToDurableFailures(
        int dispositionValue,
        string expectedCode,
        int expectedStatus,
        string? expectedEtag)
    {
        SubscriptionRecord subscription = Subscription(version: 7);
        FakeSubscriptionRepository repository = new()
        {
            SubscriptionUpdateResult = new SubscriptionMutationResult(
                (SubscriptionMutationDisposition)dispositionValue,
                WasChanged: false,
                Value: null,
                BeforeState: null,
                CurrentVersion: 8),
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionCommandOutcome<SubscriptionView>> result =
            await environment.Service.ExecuteAsync(
                SubscriptionUpdateCommand(subscription),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedStatus, result.Error.Presentation!.Status);
        Assert.Equal(expectedEtag, result.Error.ETag);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(false, SubscriptionErrorCodes.IdempotencyConflict)]
    [InlineData(true, SubscriptionErrorCodes.CoordinationUnavailable)]
    public async Task IdempotencyConflictAndBusyStopBeforeRepositoryMutation(
        bool busy,
        string expectedCode)
    {
        RecordingIdempotencyStore idempotency = new()
        {
            ForcedAcquireResult = busy
                ? CommandIdempotencyAcquireResult.Busy
                : CommandIdempotencyAcquireResult.Conflict,
        };
        FakeSubscriptionRepository repository = new();
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> result =
            await environment.Service.ExecuteAsync(
                TemplateCreateCommand(EntityId.New()),
                TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(0, repository.TemplateCreateCalls);
        Assert.Empty(idempotency.Completions);
        Assert.Equal(0, environment.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task FailedTemplateReplayReturnsOriginalErrorWithoutAnotherMutation()
    {
        SubscriptionTemplateRecord template = Template(version: 3);
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = new TemplateMutationResult(
                SubscriptionMutationDisposition.VersionConflict,
                WasChanged: false,
                Value: null,
                BeforeState: null,
                CurrentVersion: 4),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            ReplayCompletedRequests = true,
        };
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);
        UpdateSubscriptionTemplateCommand command = TemplateUpdateCommand(template);

        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> initial =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);
        Result<SubscriptionCommandOutcome<SubscriptionTemplateView>> replay =
            await environment.Service.ExecuteAsync(command, TestContext.Current.CancellationToken);

        Assert.True(initial.IsFailure);
        Assert.True(replay.IsFailure);
        Assert.Equal(SubscriptionErrorCodes.VersionConflict, replay.Error.Code);
        Assert.Equal("\"v4\"", replay.Error.ETag);
        Assert.Equal(1, repository.TemplateUpdateCalls);
        Assert.Equal(1, environment.UnitOfWork.CommitCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LostIdempotencyCompletionPreventsCommit(bool successfulMutation)
    {
        SubscriptionTemplateRecord template = Template(version: 2);
        FakeSubscriptionRepository repository = new()
        {
            TemplateUpdateResult = successfulMutation
                ? new TemplateMutationResult(
                    SubscriptionMutationDisposition.Updated,
                    WasChanged: false,
                    template,
                    BeforeState: null,
                    CurrentVersion: 2)
                : new TemplateMutationResult(
                    SubscriptionMutationDisposition.ResourceConflict,
                    WasChanged: false,
                    Value: null,
                    BeforeState: null,
                    CurrentVersion: 2),
        };
        RecordingIdempotencyStore idempotency = new()
        {
            CompleteSucceeds = false,
        };
        TestCommandEnvironment environment = new(repository, idempotency: idempotency);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.Service.ExecuteAsync(
                TemplateUpdateCommand(template),
                TestContext.Current.CancellationToken).ConfigureAwait(true)).ConfigureAwait(true);

        Assert.Equal(0, environment.UnitOfWork.CommitCalls);
        Assert.Single(idempotency.Completions);
    }

    [Fact]
    public async Task EffectiveAccessMapsTheActiveLifecycleStatus()
    {
        SubscriptionRecord subscription = Subscription(version: 7) with
        {
            EffectiveStatus = SubscriptionEffectiveLifecycle.Active,
        };
        FakeSubscriptionRepository repository = new()
        {
            EffectiveAccessResult = subscription,
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionAccessSnapshot> result = await environment.Service.GetEffectiveAccessAsync(
            subscription.UserId,
            subscription.GroupId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(SubscriptionEffectiveStatus.Active, result.Value.EffectiveStatus);
        Assert.Equal(subscription.Id, result.Value.SubscriptionId);
        Assert.Equal(subscription.ObservedAt, result.Value.ObservedAt);
    }

    [Theory]
    [InlineData(SubscriptionEffectiveLifecycle.Scheduled)]
    [InlineData(SubscriptionEffectiveLifecycle.Expired)]
    [InlineData(SubscriptionEffectiveLifecycle.Suspended)]
    [InlineData(SubscriptionEffectiveLifecycle.Revoked)]
    public async Task EffectiveAccessRejectsEveryInactiveLifecycleStatus(
        SubscriptionEffectiveLifecycle source)
    {
        SubscriptionRecord subscription = Subscription(version: 7) with
        {
            EffectiveStatus = source,
        };
        FakeSubscriptionRepository repository = new()
        {
            EffectiveAccessResult = subscription,
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionAccessSnapshot> result = await environment.Service.GetEffectiveAccessAsync(
            subscription.UserId,
            subscription.GroupId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("subscription_required", result.Error.Code);
    }

    [Fact]
    public async Task EffectiveAccessMissingAndActiveGrantListAreMapped()
    {
        EntityId userId = EntityId.New();
        SubscriptionRecord subscription = Subscription(version: 3) with
        {
            UserId = userId,
        };
        FakeSubscriptionRepository repository = new()
        {
            EffectiveAccessResult = null,
            ActiveSubscriptionsResult = [subscription],
        };
        TestCommandEnvironment environment = new(repository);

        Result<SubscriptionAccessSnapshot> missing = await environment.Service.GetEffectiveAccessAsync(
            userId,
            subscription.GroupId,
            TestContext.Current.CancellationToken);
        Result<IReadOnlyList<UserSubscriptionGrantSnapshot>> active =
            await environment.Service.ListActiveAsync(
                userId,
                TestContext.Current.CancellationToken);

        Assert.True(missing.IsFailure);
        Assert.Equal("subscription_required", missing.Error.Code);
        Assert.True(active.IsSuccess);
        UserSubscriptionGrantSnapshot grant = Assert.Single(active.Value);
        Assert.Equal(subscription.Id, grant.SubscriptionId);
        Assert.Equal(subscription.GroupId, grant.GroupId);
        Assert.Equal(subscription.ExpiresAt, grant.ExpiresAt);
        Assert.Equal(1, repository.EffectiveAccessCalls);
        Assert.Equal(1, repository.ActiveSubscriptionsCalls);
    }

    [Fact]
    public void SubscriptionInputNormalizesValidValuesAndPolicyCopiesPepper()
    {
        Assert.Equal("key", SubscriptionInput.IdempotencyKey("key"));
        Assert.Equal("Template", SubscriptionInput.Name("  Template  "));
        Assert.Null(SubscriptionInput.Description(null));
        Assert.Equal("", SubscriptionInput.Description(string.Empty));
        Assert.Equal(3650, SubscriptionInput.DurationDays(3650));
        Assert.Equal("reason", SubscriptionInput.Reason(" reason "));
        SubscriptionInput.ExpectedVersion(1);
        SubscriptionInput.TimeRange(CreatedAt, CreatedAt.AddTicks(1));

        byte[] pepper = Enumerable.Repeat((byte)7, 32).ToArray();
        SubscriptionPolicy policy = new(pepper);
        Assert.NotSame(pepper, policy.RequestHashPepper);
        pepper[0] = 0;
        Assert.Equal(7, policy.RequestHashPepper[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void SubscriptionInputAndPolicyRejectInvalidBoundaries(int scenario)
    {
        Action action = scenario switch
        {
            0 => () => SubscriptionInput.IdempotencyKey(string.Empty),
            1 => () => SubscriptionInput.IdempotencyKey("contains space"),
            2 => () => SubscriptionInput.IdempotencyKey(new string('k', 129)),
            3 => () => SubscriptionInput.Name("\u0001"),
            4 => () => SubscriptionInput.Description(new string('d', 1001)),
            5 => () => SubscriptionInput.DurationDays(0),
            6 => () => SubscriptionInput.DurationDays(3651),
            7 => () => SubscriptionInput.Reason(" "),
            8 => () => SubscriptionInput.ExpectedVersion(0),
            9 => () => SubscriptionInput.TimeRange(CreatedAt, CreatedAt),
            _ => () => _ = new SubscriptionPolicy(new byte[31]),
        };

        Assert.ThrowsAny<ArgumentException>(action);
    }

    [Fact]
    public void SubscriptionPolicyRejectsNullPepper()
    {
        Assert.Throws<ArgumentNullException>(() => _ = new SubscriptionPolicy(null!));
    }

    private static CreateSubscriptionTemplateCommand TemplateCreateCommand(
        EntityId groupId) => new(
            EntityId.New(),
            new SubscriptionActor(EntityId.New(), SystemRole.Admin, 1),
            "template-create-key",
            groupId,
            "Stable template",
            "Access only",
            30,
            "192.0.2.10",
            "unit-test");

    private static RetireSubscriptionTemplateCommand TemplateRetireCommand(
        EntityId templateId,
        long expectedVersion) => new(
            EntityId.New(),
            Actor(),
            "template-retire-key",
            templateId,
            expectedVersion,
            "retire template",
            "192.0.2.10",
            "unit-test");

    private static AssignSubscriptionCommand SubscriptionAssignCommand(
        EntityId userId,
        EntityId templateId) => new(
            EntityId.New(),
            new SubscriptionActor(EntityId.New(), SystemRole.Admin, 1),
            "subscription-assign-key",
            userId,
            templateId,
            CreatedAt,
            CreatedAt.AddDays(30),
            "grant access",
            "192.0.2.10",
            "unit-test");

    private static UpdateSubscriptionTemplateCommand TemplateUpdateCommand(
        SubscriptionTemplateRecord template) => new(
            EntityId.New(),
            new SubscriptionActor(EntityId.New(), SystemRole.Admin, 1),
            "template-update-key",
            template.Id,
            template.Version,
            NameSpecified: true,
            template.Name,
            DescriptionSpecified: false,
            Description: null,
            DefaultDurationDaysSpecified: false,
            DefaultDurationDays: null,
            StatusSpecified: false,
            Status: null,
            Reason: null,
            IpAddress: "192.0.2.10",
            UserAgent: "unit-test");

    private static UpdateSubscriptionCommand SubscriptionUpdateCommand(
        SubscriptionRecord subscription) => new(
            EntityId.New(),
            new SubscriptionActor(EntityId.New(), SystemRole.Admin, 1),
            "subscription-update-key",
            subscription.Id,
            subscription.Version,
            StartsAtSpecified: false,
            StartsAt: null,
            ExpiresAtSpecified: false,
            ExpiresAt: null,
            StatusSpecified: true,
            Status: subscription.Status,
            Reason: "reviewed no-op",
            IpAddress: "192.0.2.10",
            UserAgent: "unit-test");

    private static SubscriptionTemplateRecord Template(long version) => new(
        EntityId.New(),
        EntityId.New(),
        "Stable template",
        "Access only",
        30,
        SubscriptionTemplateLifecycle.Active,
        version,
        CreatedAt,
        UpdatedAt);

    private static SubscriptionRecord Subscription(long version) => new(
        EntityId.New(),
        EntityId.New(),
        EntityId.New(),
        EntityId.New(),
        "Stable plan snapshot",
        CreatedAt,
        CreatedAt.AddDays(30),
        SubscriptionLifecycle.Active,
        SubscriptionEffectiveLifecycle.Active,
        EntityId.New(),
        version,
        CreatedAt,
        UpdatedAt,
        UpdatedAt.AddSeconds(5));

    private static SubscriptionActor Actor(
        SystemRole role = SystemRole.Admin,
        long tokenVersion = 1) => new(
            EntityId.New(),
            role,
            tokenVersion);

    private static RecordingUserStatusReader ActiveUser(EntityId userId) => new(
        Result.Success(new UserStatusSnapshot(
            userId,
            UserLifecycle.Active,
            SystemRole.User,
            3,
            7,
            UpdatedAt)));

    private sealed class TestCommandEnvironment
    {
        internal TestCommandEnvironment(
            FakeSubscriptionRepository repository,
            RecordingUserStatusReader? users = null,
            RecordingIdempotencyStore? idempotency = null)
        {
            UnitOfWork = new RecordingUnitOfWorkFactory();
            Idempotency = idempotency ?? new RecordingIdempotencyStore();
            Audit = new RecordingAuditAppender();
            Outbox = new RecordingOutboxAppender();
            Users = users ?? new RecordingUserStatusReader(Result.Success(
                new UserStatusSnapshot(
                    EntityId.New(),
                    UserLifecycle.Active,
                    SystemRole.User,
                    1,
                    1,
                    UpdatedAt)));
            Service = new SubscriptionUseCaseService(
                repository,
                Users,
                UnitOfWork,
                Idempotency,
                Audit,
                Outbox,
                new SubscriptionPolicy(new byte[32]));
        }

        internal SubscriptionUseCaseService Service { get; }

        internal RecordingUnitOfWorkFactory UnitOfWork { get; }

        internal RecordingIdempotencyStore Idempotency { get; }

        internal RecordingAuditAppender Audit { get; }

        internal RecordingOutboxAppender Outbox { get; }

        internal RecordingUserStatusReader Users { get; }
    }

    private sealed class FakeSubscriptionRepository : ISubscriptionRepository
    {
        internal TemplateMutationResult? TemplateUpdateResult { get; set; }

        internal TemplateMutationResult? TemplateCreateResult { get; set; }

        internal SubscriptionMutationResult? SubscriptionUpdateResult { get; set; }

        internal SubscriptionMutationResult? SubscriptionAssignResult { get; set; }

        internal SubscriptionTemplateSlice TemplateListResult { get; set; } = new([], false);

        internal SubscriptionTemplateRecord? TemplateGetResult { get; set; }

        internal SubscriptionSlice SubscriptionListResult { get; set; } = new([], false);

        internal SubscriptionRecord? SubscriptionGetResult { get; set; }

        internal SubscriptionRecord? EffectiveAccessResult { get; set; }

        internal IReadOnlyList<SubscriptionRecord> UserSubscriptionsResult { get; set; } = [];

        internal IReadOnlyList<SubscriptionRecord> ActiveSubscriptionsResult { get; set; } = [];

        internal int TemplateCreateCalls { get; private set; }

        internal int TemplateUpdateCalls { get; private set; }

        internal int TemplateListCalls { get; private set; }

        internal int TemplateGetCalls { get; private set; }

        internal int SubscriptionAssignCalls { get; private set; }

        internal int SubscriptionUpdateCalls { get; private set; }

        internal int SubscriptionListCalls { get; private set; }

        internal int SubscriptionGetCalls { get; private set; }

        internal int UserSubscriptionsCalls { get; private set; }

        internal int EffectiveAccessCalls { get; private set; }

        internal int ActiveSubscriptionsCalls { get; private set; }

        internal bool LastAllowRevokedRegrant { get; private set; }

        internal bool LastTemplateRetire { get; private set; }

        internal string? LastTemplateCreateName { get; private set; }

        internal string? LastTemplateUpdateName { get; private set; }

        internal string? LastTemplateUpdateReason { get; private set; }

        internal string? LastSubscriptionReason { get; private set; }

        internal SubscriptionCursor? LastTemplateCursor { get; private set; }

        internal SubscriptionCursor? LastSubscriptionCursor { get; private set; }

        internal EntityId? LastSubscriptionUserId { get; private set; }

        internal EntityId? LastSubscriptionGroupId { get; private set; }

        internal EntityId? LastVisibleToUserId { get; private set; }

        public ValueTask<TemplateMutationResult> UpdateTemplateAsync(
            EntityId templateId,
            long expectedVersion,
            bool nameSpecified,
            string? name,
            bool descriptionSpecified,
            string? description,
            bool durationSpecified,
            int? durationDays,
            bool statusSpecified,
            SubscriptionTemplateLifecycle? status,
            bool retire,
            string? reason,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateUpdateCalls++;
            LastTemplateUpdateName = name;
            LastTemplateUpdateReason = reason;
            LastTemplateRetire = retire;
            return ValueTask.FromResult(TemplateUpdateResult ?? throw Unexpected());
        }

        public ValueTask<SubscriptionMutationResult> UpdateSubscriptionAsync(
            EntityId subscriptionId,
            long expectedVersion,
            bool startsAtSpecified,
            DateTimeOffset? startsAt,
            bool expiresAtSpecified,
            DateTimeOffset? expiresAt,
            bool statusSpecified,
            SubscriptionLifecycle? status,
            bool allowRevokedRegrant,
            EntityId actorId,
            string reason,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionUpdateCalls++;
            LastAllowRevokedRegrant = allowRevokedRegrant;
            LastSubscriptionReason = reason;
            return ValueTask.FromResult(SubscriptionUpdateResult ?? throw Unexpected());
        }

        public ValueTask<SubscriptionTemplateSlice> ListTemplatesAsync(
            SubscriptionCursor? cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateListCalls++;
            LastTemplateCursor = cursor;
            return ValueTask.FromResult(TemplateListResult);
        }

        public ValueTask<SubscriptionTemplateRecord?> GetTemplateAsync(
            EntityId templateId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateGetCalls++;
            return ValueTask.FromResult(TemplateGetResult);
        }

        public ValueTask<TemplateMutationResult> CreateTemplateAsync(
            EntityId templateId,
            EntityId groupId,
            string name,
            string? description,
            int defaultDurationDays,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateCreateCalls++;
            LastTemplateCreateName = name;
            return ValueTask.FromResult(TemplateCreateResult ?? throw Unexpected());
        }

        public ValueTask<SubscriptionSlice> ListSubscriptionsAsync(
            SubscriptionCursor? cursor,
            int limit,
            EntityId? userId,
            EntityId? groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionListCalls++;
            LastSubscriptionCursor = cursor;
            LastSubscriptionUserId = userId;
            LastSubscriptionGroupId = groupId;
            return ValueTask.FromResult(SubscriptionListResult);
        }

        public ValueTask<SubscriptionRecord?> GetSubscriptionAsync(
            EntityId subscriptionId,
            EntityId? visibleToUserId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionGetCalls++;
            LastVisibleToUserId = visibleToUserId;
            return ValueTask.FromResult(SubscriptionGetResult);
        }

        public ValueTask<SubscriptionRecord?> GetEffectiveAccessAsync(
            EntityId userId,
            EntityId groupId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EffectiveAccessCalls++;
            return ValueTask.FromResult(EffectiveAccessResult);
        }

        public ValueTask<IReadOnlyList<SubscriptionRecord>> ListForUserAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UserSubscriptionsCalls++;
            return ValueTask.FromResult(UserSubscriptionsResult);
        }

        public ValueTask<IReadOnlyList<SubscriptionRecord>> ListActiveForUserAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveSubscriptionsCalls++;
            return ValueTask.FromResult(ActiveSubscriptionsResult);
        }

        public ValueTask<SubscriptionMutationResult> AssignSubscriptionAsync(
            EntityId subscriptionId,
            EntityId userId,
            EntityId templateId,
            DateTimeOffset? startsAt,
            DateTimeOffset? expiresAt,
            EntityId assignedBy,
            string reason,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SubscriptionAssignCalls++;
            LastSubscriptionReason = reason;
            return ValueTask.FromResult(SubscriptionAssignResult ?? throw Unexpected());
        }
    }

    private sealed class RecordingUserStatusReader(
        Result<UserStatusSnapshot> currentResult) : IUserStatusReader
    {
        internal Result<UserStatusSnapshot> CurrentResult { get; set; } = currentResult;

        internal int Calls { get; private set; }

        public ValueTask<Result<UserStatusSnapshot>> GetCurrentAsync(
            EntityId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(CurrentResult);
        }
    }

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int CommitCalls { get; private set; }

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        private CommandIdempotencyResponse? _completedResponse;

        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        internal bool ReplayCompletedRequests { get; init; }

        internal CommandIdempotencyAcquireResult? ForcedAcquireResult { get; init; }

        internal bool CompleteSucceeds { get; init; } = true;

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ForcedAcquireResult is not null)
            {
                return ValueTask.FromResult(ForcedAcquireResult);
            }

            if (ReplayCompletedRequests && _completedResponse is not null)
            {
                return ValueTask.FromResult(CommandIdempotencyAcquireResult.Replay(
                    _completedResponse));
            }

            return ValueTask.FromResult(CommandIdempotencyAcquireResult.Acquired(
                new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    EntityId.New(),
                    Generation: 1,
                    Version: 1)));
        }

        public ValueTask<bool> HeartbeatAsync(
            CommandIdempotencyHeartbeat heartbeat,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();

        public ValueTask<bool> CompleteAsync(
            CommandIdempotencyCompletion completion,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Completions.Add(completion);
            _completedResponse = new CommandIdempotencyResponse(
                completion.TerminalStatus,
                completion.ResponseStatus,
                completion.ResponseBody,
                completion.ResponseBodyEnvelope,
                completion.ResponseHeaders,
                completion.ResourceType,
                completion.ResourceId);
            return ValueTask.FromResult(CompleteSucceeds);
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

    private static InvalidOperationException Unexpected() => new(
        "The test invoked an unexpected SubscriptionAccess dependency path.");
}
