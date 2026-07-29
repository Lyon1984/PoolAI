#pragma warning disable MA0051 // Coordinator protocol tests intentionally keep their fakes local.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application;

namespace PoolAI.UnitTests;

public sealed class SupplyCommandCoordinatorTests
{
    [Fact]
    public async Task AcquireBuildsDeterministicDomainSeparatedRequest()
    {
        CoordinatorFixture fixture = new();
        EntityId requestId = EntityId.New();
        AccountActor actor = Actor(AccountControlRole.Admin);
        object body = new { value = "same", count = 2 };

        await fixture.Coordinator.AcquireAsync(
            "scope",
            "key",
            requestId,
            actor,
            body,
            fixture.UnitOfWork.Context,
            TestContext.Current.CancellationToken);
        await fixture.Coordinator.AcquireAsync(
            "scope",
            "key",
            requestId,
            actor,
            body,
            fixture.UnitOfWork.Context,
            TestContext.Current.CancellationToken);
        await fixture.Coordinator.AcquireAsync(
            "scope",
            "key",
            requestId,
            actor,
            new { value = "different", count = 2 },
            fixture.UnitOfWork.Context,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, fixture.Idempotency.Requests.Count);
        CommandIdempotencyRequest first = fixture.Idempotency.Requests[0];
        CommandIdempotencyRequest second = fixture.Idempotency.Requests[1];
        CommandIdempotencyRequest different = fixture.Idempotency.Requests[2];
        Assert.Equal("scope", first.Scope);
        Assert.Equal("key", first.Key);
        Assert.Equal($"user:{actor.UserId.Value:D}", first.ActorFingerprint);
        Assert.Equal(requestId, first.Owner);
        Assert.Equal(TimeSpan.FromSeconds(30), first.LeaseDuration);
        Assert.Equal(TimeSpan.FromHours(24), first.Retention);
        Assert.Equal(32, first.RequestHash.Length);
        Assert.True(first.RequestHash.Span.SequenceEqual(second.RequestHash.Span));
        Assert.False(first.RequestHash.Span.SequenceEqual(different.RequestHash.Span));
        Assert.NotEqual(first.RecordId, second.RecordId);
    }

    [Fact]
    public async Task ReplayMapsConflictBusyFailureAndValidSuccess()
    {
        Result<SupplyCommandOutcome<ChannelView>> conflict =
            Assert.IsType<Result<SupplyCommandOutcome<ChannelView>>>(
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Conflict,
                expectedStatus: 201,
                resourceType: "channel"));
        AssertFailure(conflict, SupplyControlErrorCodes.IdempotencyConflict);

        Result<SupplyCommandOutcome<ChannelView>> busy =
            Assert.IsType<Result<SupplyCommandOutcome<ChannelView>>>(
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Busy,
                expectedStatus: 201,
                resourceType: "channel"));
        AssertFailure(busy, SupplyControlErrorCodes.CoordinationUnavailable);
        Assert.Equal(1, busy.Error.RetryAfterSeconds);

        Result<SupplyCommandOutcome<ChannelView>> failed =
            Assert.IsType<Result<SupplyCommandOutcome<ChannelView>>>(
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Replay(
                    FailedResponse(
                        SupplyControlErrorCodes.VersionConflict,
                        "changed",
                        "\"v4\"")),
                expectedStatus: 201,
                resourceType: "channel"));
        AssertFailure(failed, SupplyControlErrorCodes.VersionConflict);
        Assert.Equal("\"v4\"", failed.Error.ETag);

        EntityId resourceId = EntityId.New();
        ChannelView channel = Channel(resourceId, version: 9);
        CoordinatorFixture completed = new();
        await completed.Coordinator.CompleteSuccessAsync(
            Lease(),
            status: 201,
            channel,
            etag: "\"v9\"",
            location: $"/api/v1/admin/channels/{resourceId.Value:D}",
            resourceType: "channel",
            resourceId,
            completed.UnitOfWork,
            TestContext.Current.CancellationToken);
        CommandIdempotencyCompletion completion =
            Assert.Single(completed.Idempotency.Completions);
        Result<SupplyCommandOutcome<ChannelView>> replay =
            Assert.IsType<Result<SupplyCommandOutcome<ChannelView>>>(
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Replay(Response(completion)),
                expectedStatus: 201,
                resourceType: "channel",
                expectedResourceId: resourceId));

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal(channel.Id, replay.Value.Value.Id);
        Assert.Equal(channel.Name, replay.Value.Value.Name);
        Assert.Equal(channel.Version, replay.Value.Value.Version);
        Assert.Equal("\"v9\"", replay.Value.ETag);
        Assert.Equal(
            $"/api/v1/admin/channels/{resourceId.Value:D}",
            replay.Value.Location);
    }

    [Fact]
    public void ReplayRejectsCorruptSuccessMetadata()
    {
        EntityId resourceId = EntityId.New();
        ChannelView channel = Channel(resourceId, version: 2);
        CommandIdempotencyResponse valid = new(
            CommandIdempotencyTerminalStatus.Completed,
            Status: 200,
            Body: ChannelReplayBody(channel),
            BodyEnvelope: null,
            Headers: Headers("\"v2\""),
            ResourceType: "channel",
            ResourceId: resourceId);
        CommandIdempotencyResponse[] corrupt =
        [
            valid with { Status = 201 },
            valid with { Body = null },
            valid with { BodyEnvelope = JsonSerializer.SerializeToElement(new { }) },
            valid with { Headers = JsonSerializer.SerializeToElement(new { }) },
            valid with { ResourceType = "account" },
            valid with { ResourceId = EntityId.New() },
        ];

        foreach (CommandIdempotencyResponse response in corrupt)
        {
            Assert.Throws<InvalidOperationException>(() =>
                GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                    CommandIdempotencyAcquireResult.Replay(response),
                    expectedStatus: 200,
                    resourceType: "channel",
                    expectedResourceId: resourceId));
        }

        Assert.Throws<InvalidOperationException>(() =>
            GroupSupplyCommandCoordinator.ReplayOrFailure<ChannelView>(
                CommandIdempotencyAcquireResult.Replay(
                    new CommandIdempotencyResponse(
                        CommandIdempotencyTerminalStatus.Failed,
                        Status: 422,
                        Body: null,
                        BodyEnvelope: null,
                        Headers: Headers("\"v1\""),
                        ResourceType: null,
                        ResourceId: null)),
                expectedStatus: 200,
                resourceType: "channel"));
    }

    [Fact]
    public void RetireReplayValidatesResourceAndBodyShape()
    {
        EntityId resourceId = EntityId.New();
        Result<SupplyCommandOutcome> replay =
            Assert.IsType<Result<SupplyCommandOutcome>>(
                GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                CommandIdempotencyAcquireResult.Replay(
                    new CommandIdempotencyResponse(
                        CommandIdempotencyTerminalStatus.Completed,
                        Status: 204,
                        Body: null,
                        BodyEnvelope: null,
                        Headers: Headers("\"v5\""),
                        ResourceType: "channel",
                        ResourceId: resourceId)),
                "channel",
                resourceId));

        Assert.True(replay.IsSuccess);
        Assert.True(replay.Value.IsReplay);
        Assert.Equal("\"v5\"", replay.Value.ETag);

        Result<SupplyCommandOutcome> failed =
            Assert.IsType<Result<SupplyCommandOutcome>>(
                GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                CommandIdempotencyAcquireResult.Replay(
                    FailedResponse(
                        SupplyControlErrorCodes.ChannelInUse,
                        "blocked",
                        etag: null)),
                "channel",
                resourceId));
        AssertFailure(failed, "channel_in_use");
        Assert.Null(failed.Error.ETag);

        Assert.Throws<InvalidOperationException>(() =>
            GroupSupplyCommandCoordinator.RetireReplayOrFailure(
                CommandIdempotencyAcquireResult.Replay(
                    new CommandIdempotencyResponse(
                        CommandIdempotencyTerminalStatus.Completed,
                        Status: 204,
                        Body: JsonSerializer.SerializeToElement(new { }),
                        BodyEnvelope: null,
                        Headers: Headers("\"v5\""),
                        ResourceType: "channel",
                        ResourceId: resourceId)),
                "channel",
                resourceId));
    }

    [Fact]
    public async Task CompletionMethodsPersistExactTerminalShapesBeforeCommit()
    {
        EntityId resourceId = EntityId.New();
        CommandIdempotencyLease lease = Lease();

        CoordinatorFixture success = new();
        ChannelView channel = Channel(resourceId, version: 3);
        await success.Coordinator.CompleteSuccessAsync(
            lease,
            status: 201,
            value: channel,
            etag: "\"v3\"",
            location: $"/api/v1/admin/channels/{resourceId.Value:D}",
            resourceType: "channel",
            resourceId,
            success.UnitOfWork,
            TestContext.Current.CancellationToken);
        CommandIdempotencyCompletion successCompletion =
            Assert.Single(success.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, successCompletion.TerminalStatus);
        Assert.Equal(201, successCompletion.ResponseStatus);
        Assert.Equal(channel.Name, successCompletion.ResponseBody!.Value.GetProperty("Name").GetString());
        Assert.Equal("\"v3\"", Header(successCompletion, "ETag"));
        Assert.Equal(
            $"/api/v1/admin/channels/{resourceId.Value:D}",
            Header(successCompletion, "Location"));
        Assert.Equal("channel", successCompletion.ResourceType);
        Assert.Equal(resourceId, successCompletion.ResourceId);
        Assert.Equal(1, success.UnitOfWork.CommitCalls);

        CoordinatorFixture failure = new();
        Result<string> failed = await failure.Coordinator.CompleteFailureAsync<string>(
            lease,
            new SupplyMutationFailure(
                Status: 412,
                Code: "version_conflict",
                Description: "changed",
                ETag: "\"v8\""),
            failure.UnitOfWork,
            TestContext.Current.CancellationToken);
        AssertFailure(failed, "version_conflict");
        Assert.Equal("\"v8\"", failed.Error.ETag);
        CommandIdempotencyCompletion failureCompletion =
            Assert.Single(failure.Idempotency.Completions);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, failureCompletion.TerminalStatus);
        Assert.Equal(412, failureCompletion.ResponseStatus);
        Assert.Equal(
            "version_conflict",
            failureCompletion.ResponseBody!.Value.GetProperty("Code").GetString());
        Assert.Null(failureCompletion.ResourceType);
        Assert.Null(failureCompletion.ResourceId);
        Assert.Equal(1, failure.UnitOfWork.CommitCalls);

        CoordinatorFixture retire = new();
        await retire.Coordinator.CompleteRetireAsync(
            lease,
            "\"v4\"",
            "channel",
            resourceId,
            retire.UnitOfWork,
            TestContext.Current.CancellationToken);
        CommandIdempotencyCompletion retireCompletion =
            Assert.Single(retire.Idempotency.Completions);
        Assert.Equal(204, retireCompletion.ResponseStatus);
        Assert.Null(retireCompletion.ResponseBody);
        Assert.Equal("\"v4\"", Header(retireCompletion, "ETag"));
        Assert.Equal(1, retire.UnitOfWork.CommitCalls);
    }

    [Fact]
    public async Task LostCompletionLeaseFailsClosedWithoutCommit()
    {
        CoordinatorFixture fixture = new();
        fixture.Idempotency.CompleteResult = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Coordinator.CompleteSuccessAsync(
                    Lease(),
                    200,
                    Channel(EntityId.New(), version: 1),
                    "\"v1\"",
                    location: null,
                    "channel",
                    EntityId.New(),
                    fixture.UnitOfWork,
                    TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(0, fixture.UnitOfWork.CommitCalls);
        Assert.Single(fixture.Idempotency.Completions);
    }

    [Fact]
    public async Task AuditAndEventPreserveActorMetadataAndNeverExposeIdempotencyKey()
    {
        CoordinatorFixture fixture = new();
        EntityId targetId = EntityId.New();
        EntityId requestId = EntityId.New();
        const string secretKey = "never-persist-this-key";

        foreach ((AccountControlRole role, AuditActorType expected) in new[]
                 {
                     (AccountControlRole.Admin, AuditActorType.Admin),
                     (AccountControlRole.Operator, AuditActorType.Operator),
                     (AccountControlRole.Auditor, AuditActorType.Auditor),
                     (AccountControlRole.User, AuditActorType.User),
                 })
        {
            await fixture.Coordinator.AppendAuditAsync(
                Actor(role),
                "supply.changed",
                "channel",
                targetId,
                requestId,
                reason: "reason",
                ipAddress: "127.0.0.1",
                userAgent: "test",
                before: JsonSerializer.SerializeToElement(new { version = 1 }),
                after: JsonSerializer.SerializeToElement(new { version = 2 }),
                secretKey,
                fixture.UnitOfWork.Context,
                TestContext.Current.CancellationToken);
            Assert.Equal(expected, fixture.Audit.Entries[^1].ActorType);
        }

        Assert.Equal(4, fixture.Audit.Entries.Count);
        string firstMetadata = fixture.Audit.Entries[0].Metadata.GetRawText();
        Assert.DoesNotContain(secretKey, firstMetadata, StringComparison.Ordinal);
        Assert.Equal(
            firstMetadata,
            fixture.Audit.Entries[1].Metadata.GetRawText());

        DateTimeOffset occurredAt =
            new(2026, 7, 30, 12, 30, 0, TimeSpan.Zero);
        await fixture.Coordinator.AppendEventAsync(
            "channel_updated",
            "channel",
            targetId,
            version: 2,
            requestId,
            JsonSerializer.SerializeToElement(new { version = 2 }),
            occurredAt,
            fixture.UnitOfWork.Context,
            TestContext.Current.CancellationToken);

        IntegrationEvent integrationEvent =
            Assert.Single(fixture.Outbox.Events);
        Assert.Equal("poolai.supply.v1", integrationEvent.Topic);
        Assert.Equal(1, integrationEvent.SchemaVersion);
        Assert.Equal("channel", integrationEvent.AggregateType);
        Assert.Equal(targetId, integrationEvent.AggregateId);
        Assert.Equal(2, integrationEvent.AggregateVersion);
        Assert.Equal(requestId, integrationEvent.CorrelationId);
        Assert.Equal(occurredAt, integrationEvent.OccurredAt);
        Assert.Equal("\"v27\"", GroupSupplyCommandCoordinator.ETag(27));
    }

    private static AccountActor Actor(AccountControlRole role) => new(
        EntityId.New(),
        role,
        TokenVersion: 1);

    private static CommandIdempotencyLease Lease() => new(
        "scope",
        "key",
        EntityId.New(),
        Generation: 1,
        Version: 1);

    private static CommandIdempotencyResponse FailedResponse(
        string code,
        string description,
        string? etag) => new(
        CommandIdempotencyTerminalStatus.Failed,
        Status: code switch
        {
            SupplyControlErrorCodes.ValidationFailed => 422,
            SupplyControlErrorCodes.ResourceConflict => 409,
            SupplyControlErrorCodes.ResourceNotFound => 404,
            SupplyControlErrorCodes.VersionConflict => 412,
            SupplyControlErrorCodes.ChannelInUse => 409,
            _ => throw new ArgumentOutOfRangeException(nameof(code)),
        },
        Body: JsonSerializer.SerializeToElement(
            new SupplyReplayFailure(code, description)),
        BodyEnvelope: null,
        Headers: Headers(etag),
        ResourceType: null,
        ResourceId: null);

    private static CommandIdempotencyResponse Response(
        CommandIdempotencyCompletion completion) => new(
        completion.TerminalStatus,
        completion.ResponseStatus,
        completion.ResponseBody,
        completion.ResponseBodyEnvelope,
        completion.ResponseHeaders,
        completion.ResourceType,
        completion.ResourceId);

    private static ChannelView Channel(EntityId id, long version) => new(
        id,
        "Channel",
        UpstreamProvider.OpenAiCompatible,
        ChannelLifecycle.Disabled,
        new ChannelCapabilitiesSnapshot(
            Responses: true,
            ChatCompletions: true,
            FunctionTools: true,
            Streaming: true),
        [new ChannelModelMappingView("model-a", "upstream-a")],
        version,
        new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero));

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

    private sealed class CoordinatorFixture
    {
        internal CoordinatorFixture()
        {
            Coordinator = new GroupSupplyCommandCoordinator(
                Idempotency,
                Audit,
                Outbox,
                new AccountControlPlanePolicy(
                    Enumerable.Range(1, 32)
                        .Select(static value => (byte)value)
                        .ToArray()));
        }

        internal RecordingIdempotencyStore Idempotency { get; } = new();

        internal RecordingAuditAppender Audit { get; } = new();

        internal RecordingOutboxAppender Outbox { get; } = new();

        internal RecordingUnitOfWork UnitOfWork { get; } = new();

        internal GroupSupplyCommandCoordinator Coordinator { get; }
    }

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        internal List<CommandIdempotencyRequest> Requests { get; } = [];

        internal List<CommandIdempotencyCompletion> Completions { get; } = [];

        internal bool CompleteResult { get; set; } = true;

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return ValueTask.FromResult(
                CommandIdempotencyAcquireResult.Acquired(
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
}
#pragma warning restore MA0051
