#pragma warning disable MA0051 // Atomic replay evidence is intentionally kept together.
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Application;
using PoolAI.Modules.Operations.Application.Ports;

namespace PoolAI.UnitTests;

public sealed class OperationsOutboxReplayUseCaseTests
{
    [Fact]
    public async Task AdminReplayCommitsReplacementAuditAndIdempotencyReceiptTogether()
    {
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxReplayRepository repository = new(
            static write => new OutboxReplayWriteResult(
                OutboxReplayPersistenceDisposition.Created,
                write.NewMessageId,
                EventSequence: 901));
        RecordingIdempotencyStore idempotency = new();
        RecordingAuditAppender audit = new();
        OutboxReplayService service = CreateService(
            repository,
            unitOfWorkFactory,
            idempotency,
            audit);
        ReplayDeadOutboxCommand command = ValidCommand();

        Result<OutboxReplayOutcome> result = await service.ExecuteAsync(
            command,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsReplay);
        Assert.Equal(command.SourceMessageId, result.Value.ReplayOf);
        Assert.Equal(901, result.Value.EventSequence);
        Assert.Equal(repository.LastWrite!.NewMessageId, result.Value.MessageId);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(1, unitOfWorkFactory.BeginCalls);
        Assert.Equal(1, unitOfWorkFactory.CommitCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
        Assert.Same(unitOfWorkFactory.Context, repository.LastContext);
        Assert.Same(unitOfWorkFactory.Context, idempotency.LastAcquireContext);
        Assert.Same(unitOfWorkFactory.Context, idempotency.LastCompleteContext);
        Assert.Same(unitOfWorkFactory.Context, audit.LastContext);

        CommandIdempotencyRequest acquire = Assert.IsType<CommandIdempotencyRequest>(
            idempotency.LastRequest);
        Assert.Equal(command.IdempotencyKey, acquire.Key);
        Assert.Contains(
            command.Actor.UserId.Value.ToString("D"),
            acquire.Scope,
            StringComparison.Ordinal);
        Assert.Contains(
            command.SourceMessageId.Value.ToString("D"),
            acquire.Scope,
            StringComparison.Ordinal);
        Assert.Equal(32, acquire.RequestHash.Length);

        CommandIdempotencyCompletion completion =
            Assert.IsType<CommandIdempotencyCompletion>(idempotency.LastCompletion);
        Assert.Equal(CommandIdempotencyTerminalStatus.Completed, completion.TerminalStatus);
        Assert.Equal(202, completion.ResponseStatus);
        Assert.Equal("outbox_message", completion.ResourceType);
        Assert.Equal(result.Value.MessageId, completion.ResourceId);
        Assert.Equal(
            result.Value.MessageId.Value,
            completion.ResponseBody!.Value.GetProperty("MessageId").GetGuid());
        Assert.Equal(
            "901",
            completion.ResponseBody.Value.GetProperty("EventSequence").GetString());

        AuditEntry entry = Assert.IsType<AuditEntry>(audit.LastEntry);
        Assert.Equal(AuditActorType.Admin, entry.ActorType);
        Assert.Equal(command.Actor.UserId, entry.ActorUserId);
        Assert.Equal("outbox.dead.replayed", entry.Action);
        Assert.Equal("outbox_message", entry.TargetType);
        Assert.Equal(result.Value.MessageId, entry.TargetId);
        Assert.Equal(command.RequestId, entry.RequestId);
        Assert.Equal(command.Reason, entry.Reason);
        Assert.Equal("dead", entry.BeforeState!.Value.GetProperty("status").GetString());
        Assert.Equal("pending", entry.AfterState!.Value.GetProperty("status").GetString());
        Assert.False(entry.Metadata.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task TerminalIdempotencyReplayBypassesRepositoryAuditAndCommit()
    {
        EntityId messageId = EntityId.New();
        EntityId sourceMessageId = EntityId.New();
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            MessageId = messageId.Value,
            EventSequence = "902",
            ReplayOf = sourceMessageId.Value,
            Status = "pending",
        });
        RecordingIdempotencyStore idempotency = new(
            CommandIdempotencyAcquireResult.Replay(new CommandIdempotencyResponse(
                CommandIdempotencyTerminalStatus.Completed,
                202,
                body,
                BodyEnvelope: null,
                EmptyObject(),
                "outbox_message",
                messageId)));
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        OutboxReplayService service = CreateService(
            new ThrowingOutboxReplayRepository(),
            unitOfWorkFactory,
            idempotency,
            new ThrowingAuditAppender());

        Result<OutboxReplayOutcome> result = await service.ExecuteAsync(
            ValidCommand() with { SourceMessageId = sourceMessageId },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsReplay);
        Assert.Equal(messageId, result.Value.MessageId);
        Assert.Equal(sourceMessageId, result.Value.ReplayOf);
        Assert.Equal(902, result.Value.EventSequence);
        Assert.Equal(1, unitOfWorkFactory.BeginCalls);
        Assert.Equal(0, unitOfWorkFactory.CommitCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
        Assert.Null(idempotency.LastCompletion);
    }

    [Fact]
    public async Task TerminalReplayWithDifferentSourceFailsClosed()
    {
        EntityId messageId = EntityId.New();
        JsonElement body = JsonSerializer.SerializeToElement(new
        {
            MessageId = messageId.Value,
            EventSequence = "903",
            ReplayOf = EntityId.New().Value,
            Status = "pending",
        });
        RecordingIdempotencyStore idempotency = new(
            CommandIdempotencyAcquireResult.Replay(new CommandIdempotencyResponse(
                CommandIdempotencyTerminalStatus.Completed,
                202,
                body,
                BodyEnvelope: null,
                EmptyObject(),
                "outbox_message",
                messageId)));
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        OutboxReplayService service = CreateService(
            new ThrowingOutboxReplayRepository(),
            unitOfWorkFactory,
            idempotency,
            new ThrowingAuditAppender());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ExecuteAsync(
                ValidCommand(),
                TestContext.Current.CancellationToken).ConfigureAwait(false))
            .ConfigureAwait(true);

        Assert.Equal("The stored Outbox replay response is invalid.", error.Message);
        Assert.Equal(0, unitOfWorkFactory.CommitCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
    }

    [Theory]
    [InlineData("not-found", 404, "resource_not_found")]
    [InlineData("not-dead", 409, "resource_conflict")]
    public async Task SourceFailuresBecomeDurableIdempotentFailures(
        string scenario,
        int expectedStatus,
        string expectedCode)
    {
        OutboxReplayPersistenceDisposition disposition = scenario switch
        {
            "not-found" => OutboxReplayPersistenceDisposition.SourceNotFound,
            "not-dead" => OutboxReplayPersistenceDisposition.SourceNotDead,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingIdempotencyStore idempotency = new();
        OutboxReplayService service = CreateService(
            new RecordingOutboxReplayRepository(
                _ => new OutboxReplayWriteResult(disposition)),
            unitOfWorkFactory,
            idempotency,
            new ThrowingAuditAppender());

        Result<OutboxReplayOutcome> result = await service.ExecuteAsync(
            ValidCommand(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(expectedStatus, result.Error.Presentation!.Status);
        Assert.Equal(1, unitOfWorkFactory.CommitCalls);
        CommandIdempotencyCompletion completion =
            Assert.IsType<CommandIdempotencyCompletion>(idempotency.LastCompletion);
        Assert.Equal(CommandIdempotencyTerminalStatus.Failed, completion.TerminalStatus);
        Assert.Equal(expectedStatus, completion.ResponseStatus);
        Assert.Null(completion.ResourceType);
        Assert.Null(completion.ResourceId);
    }

    [Theory]
    [InlineData(OperationsControlRole.User, "valid reason", "role_required")]
    [InlineData(OperationsControlRole.Admin, " ", "validation_failed")]
    public async Task AuthorizationAndValidationFailBeforeOpeningAUnitOfWork(
        OperationsControlRole role,
        string reason,
        string expectedCode)
    {
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        OutboxReplayService service = CreateService(
            new ThrowingOutboxReplayRepository(),
            unitOfWorkFactory,
            new RecordingIdempotencyStore(),
            new ThrowingAuditAppender());

        Result<OutboxReplayOutcome> result = await service.ExecuteAsync(
            ValidCommand() with
            {
                Actor = new OutboxReplayActor(EntityId.New(), role, TokenVersion: 1),
                Reason = reason,
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
        Assert.Equal(0, unitOfWorkFactory.BeginCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidReplayReasons))]
    public void ReplayReasonRejectsControlsMalformedScalarsAndInvalidLength(string? reason)
    {
        Assert.False(OutboxReplayInput.TryNormalizeReason(reason, out string normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void ReplayReasonRejectsMalformedUtf16WithoutTheoryDataSerialization()
    {
        string isolatedHighSurrogate = new('\ud800', 1);
        string isolatedLowSurrogate = new('\udfff', 1);

        Assert.False(OutboxReplayInput.TryNormalizeReason(
            isolatedHighSurrogate + "replay",
            out string highNormalized));
        Assert.Equal(string.Empty, highNormalized);

        Assert.False(OutboxReplayInput.TryNormalizeReason(
            "replay" + isolatedLowSurrogate,
            out string lowNormalized));
        Assert.Equal(string.Empty, lowNormalized);
    }

    [Fact]
    public void ReplayReasonCountsScalarsAndTrimsValidUnicodeWhitespace()
    {
        string exactBoundary = string.Concat(Enumerable.Repeat("🔧", 500));
        Assert.True(OutboxReplayInput.TryNormalizeReason(
            exactBoundary,
            out string boundaryNormalized));
        Assert.Equal(exactBoundary, boundaryNormalized);

        Assert.True(OutboxReplayInput.TryNormalizeReason(
            "\u00A0修复 🔧\u3000",
            out string trimmed));
        Assert.Equal("修复 🔧", trimmed);

        Assert.True(OutboxReplayInput.TryNormalizeReason(
            "第一行\u2028第二行\u2029结束",
            out string separated));
        Assert.Equal("第一行\u2028第二行\u2029结束", separated);
    }

    [Fact]
    public async Task ReplayIdentityCollisionIsRetriedThreeTimesThenRolledBack()
    {
        RecordingUnitOfWorkFactory unitOfWorkFactory = new();
        RecordingOutboxReplayRepository repository = new(
            _ => new OutboxReplayWriteResult(
                OutboxReplayPersistenceDisposition.ReplayConflict));
        OutboxReplayService service = CreateService(
            repository,
            unitOfWorkFactory,
            new RecordingIdempotencyStore(),
            new ThrowingAuditAppender());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.ExecuteAsync(
                ValidCommand(),
                TestContext.Current.CancellationToken).ConfigureAwait(false))
            .ConfigureAwait(true);

        Assert.Contains("collision-free", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, repository.Calls);
        Assert.Equal(0, unitOfWorkFactory.CommitCalls);
        Assert.Equal(1, unitOfWorkFactory.DisposeCalls);
    }

    private static OutboxReplayService CreateService(
        IOutboxReplayRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender) => new(
        repository,
        unitOfWorkFactory,
        idempotencyStore,
        auditAppender,
        new OutboxReplayPolicy(new byte[32]));

    private static ReplayDeadOutboxCommand ValidCommand() => new(
        EntityId.New(),
        new OutboxReplayActor(EntityId.New(), OperationsControlRole.Admin, TokenVersion: 1),
        "m3-e4-replay-key",
        EntityId.New(),
        "replay after poison-message remediation",
        "192.0.2.14",
        "sha256:test");

    public static TheoryData<string?> InvalidReplayReasons => new()
    {
        null!,
        "",
        "\u00A0\u1680\u2007\u202F\u205F\u3000",
        "\u2028\u2029",
        string.Concat(Enumerable.Repeat("🔧", 501)),
        " " + string.Concat(Enumerable.Repeat("🔧", 500)),
        "\u0000replay",
        "replay\u001f",
        "\u007freplay",
        "replay\u0085",
        "replay\u009f",
    };

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class RecordingUnitOfWorkFactory : IUnitOfWorkFactory
    {
        internal int BeginCalls { get; private set; }

        internal int CommitCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal IUnitOfWorkContext Context { get; } = new UnitOfWorkContext();

        public ValueTask<IUnitOfWork> BeginAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginCalls++;
            return ValueTask.FromResult<IUnitOfWork>(new UnitOfWork(this));
        }

        private sealed class UnitOfWork(RecordingUnitOfWorkFactory owner) : IUnitOfWork
        {
            public IUnitOfWorkContext Context => owner.Context;

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
        }

        private sealed class UnitOfWorkContext : IUnitOfWorkContext;
    }

    private sealed class RecordingOutboxReplayRepository(
        Func<OutboxReplayWrite, OutboxReplayWriteResult> result) : IOutboxReplayRepository
    {
        internal int Calls { get; private set; }

        internal OutboxReplayWrite? LastWrite { get; private set; }

        internal IUnitOfWorkContext? LastContext { get; private set; }

        public ValueTask<OutboxReplayWriteResult> ReplayDeadAsync(
            OutboxReplayWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastWrite = write;
            LastContext = unitOfWorkContext;
            return ValueTask.FromResult(result(write));
        }
    }

    private sealed class ThrowingOutboxReplayRepository : IOutboxReplayRepository
    {
        public ValueTask<OutboxReplayWriteResult> ReplayDeadAsync(
            OutboxReplayWrite write,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private sealed class RecordingIdempotencyStore : ICommandIdempotencyStore
    {
        private readonly CommandIdempotencyAcquireResult? _configuredAcquire;

        internal RecordingIdempotencyStore(
            CommandIdempotencyAcquireResult? configuredAcquire = null)
        {
            _configuredAcquire = configuredAcquire;
        }

        internal CommandIdempotencyRequest? LastRequest { get; private set; }

        internal CommandIdempotencyCompletion? LastCompletion { get; private set; }

        internal IUnitOfWorkContext? LastAcquireContext { get; private set; }

        internal IUnitOfWorkContext? LastCompleteContext { get; private set; }

        public ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
            CommandIdempotencyRequest request,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastAcquireContext = unitOfWorkContext;
            return ValueTask.FromResult(_configuredAcquire
                ?? CommandIdempotencyAcquireResult.Acquired(new CommandIdempotencyLease(
                    request.Scope,
                    request.Key,
                    request.Owner,
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
            LastCompletion = completion;
            LastCompleteContext = unitOfWorkContext;
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecordingAuditAppender : IAuditAppender
    {
        internal AuditEntry? LastEntry { get; private set; }

        internal IUnitOfWorkContext? LastContext { get; private set; }

        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastEntry = entry;
            LastContext = unitOfWorkContext;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingAuditAppender : IAuditAppender
    {
        public ValueTask AppendAsync(
            AuditEntry entry,
            IUnitOfWorkContext unitOfWorkContext,
            CancellationToken cancellationToken) => throw Unexpected();
    }

    private static InvalidOperationException Unexpected() => new(
        "The operation should have short-circuited before this dependency was called.");
}
#pragma warning restore MA0051
