#pragma warning disable MA0051 // The complete transactional command sequence stays visible.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Application.Ports;

namespace PoolAI.Modules.Operations.Application;

internal sealed class OutboxReplayService : IReplayDeadOutboxUseCase
{
    private const int AcceptedStatus = 202;
    private const int NotFoundStatus = 404;
    private const int ConflictStatus = 409;
    private const int MaximumReplayIdentityAttempts = 3;
    private const string PendingStatus = "pending";
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IOutboxReplayRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly OutboxReplayPolicy _policy;

    internal OutboxReplayService(
        IOutboxReplayRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        OutboxReplayPolicy policy)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore
            ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async ValueTask<Result<OutboxReplayOutcome>> ExecuteAsync(
        ReplayDeadOutboxCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.RequestId.Value == Guid.Empty
            || command.Actor.UserId.Value == Guid.Empty
            || command.Actor.TokenVersion <= 0
            || !IsKnownRole(command.Actor.Role)
            || command.SourceMessageId.Value == Guid.Empty)
        {
            return Failure(
                OperationsErrorCodes.InvalidRequest,
                "The Outbox replay command is invalid.");
        }

        if (command.Actor.Role != OperationsControlRole.Admin)
        {
            return Failure(
                OperationsErrorCodes.RoleRequired,
                "The Admin role is required.");
        }

        if (!OutboxReplayInput.IsValidIdempotencyKey(command.IdempotencyKey))
        {
            return Failure(
                OperationsErrorCodes.InvalidRequest,
                "The idempotency key is invalid.");
        }

        if (!OutboxReplayInput.TryNormalizeReason(command.Reason, out string reason))
        {
            return Failure(
                OperationsErrorCodes.ValidationFailed,
                "The replay reason is invalid.");
        }

        PreparedReplay prepared = new(
            command,
            reason,
            Scope(command.Actor.UserId, command.SourceMessageId),
            HashRequest(command.SourceMessageId, reason));
        return await ExecutePreparedAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<OutboxReplayOutcome>> ExecutePreparedAsync(
        PreparedReplay prepared,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                prepared.Scope,
                prepared.Command.IdempotencyKey,
                EntityId.New(),
                $"user:{prepared.Command.Actor.UserId.Value:D}",
                prepared.RequestHash,
                prepared.Command.RequestId,
                IdempotencyLease,
                IdempotencyRetention),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<OutboxReplayOutcome>? early = ReplayOrAcquireFailure(
            acquire,
            prepared.Command.SourceMessageId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        OutboxReplayWriteResult write = await CreateReplacementAsync(
            prepared.Command.SourceMessageId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (write.Disposition == OutboxReplayPersistenceDisposition.SourceNotFound)
        {
            return await CompleteFailureAsync(
                lease,
                NotFoundStatus,
                OperationsErrorCodes.ResourceNotFound,
                "The source Outbox message does not exist.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (write.Disposition == OutboxReplayPersistenceDisposition.SourceNotDead)
        {
            return await CompleteFailureAsync(
                lease,
                ConflictStatus,
                OperationsErrorCodes.ResourceConflict,
                "Only a dead Outbox message can be replayed.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (write.Disposition == OutboxReplayPersistenceDisposition.ValidationFailed)
        {
            throw new InvalidOperationException(
                "The signed Outbox replay function rejected server-generated input.");
        }

        if (write.Disposition == OutboxReplayPersistenceDisposition.ReplayConflict)
        {
            throw new InvalidOperationException(
                "A collision-free Outbox replay identity could not be generated.");
        }

        EntityId messageId = write.MessageId
            ?? throw new InvalidOperationException(
                "The Outbox replay did not return a replacement message identifier.");
        long eventSequence = write.EventSequence
            ?? throw new InvalidOperationException(
                "The Outbox replay did not return an event sequence.");
        OutboxReplayOutcome outcome = new(
            IsReplay: false,
            messageId,
            eventSequence,
            prepared.Command.SourceMessageId);
        ValidateOutcome(outcome);

        await AppendAuditAsync(
            prepared,
            outcome,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await CompleteSuccessAsync(
            lease,
            outcome,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(outcome);
    }

    private async ValueTask<OutboxReplayWriteResult> CreateReplacementAsync(
        EntityId sourceMessageId,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaximumReplayIdentityAttempts; attempt++)
        {
            EntityId messageId = EntityId.New();
            OutboxReplayWriteResult write = await _repository.ReplayDeadAsync(
                new OutboxReplayWrite(
                    sourceMessageId,
                    messageId,
                    $"operations:outbox-replay:v1:{messageId.Value:D}"),
                context,
                cancellationToken).ConfigureAwait(false);
            if (write.Disposition != OutboxReplayPersistenceDisposition.ReplayConflict)
            {
                return write;
            }
        }

        return new OutboxReplayWriteResult(
            OutboxReplayPersistenceDisposition.ReplayConflict);
    }

    private static Result<OutboxReplayOutcome>? ReplayOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        EntityId sourceMessageId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure(
                OperationsErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure(
                OperationsErrorCodes.ServiceUnavailable,
                "The matching idempotent command is still in progress.",
                retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => Replay(
                acquire.Response!,
                sourceMessageId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<OutboxReplayOutcome> Replay(
        CommandIdempotencyResponse response,
        EntityId sourceMessageId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailure(response);
        }

        OutboxReplayResponseSnapshot snapshot = response.Body?
            .Deserialize<OutboxReplayResponseSnapshot>()
            ?? throw new InvalidOperationException("The Outbox replay response is invalid.");
        OutboxReplayOutcome outcome = snapshot.ToOutcome(isReplay: true);
        ValidateOutcome(outcome);
        int headerCount = response.Headers.ValueKind == JsonValueKind.Object
            ? response.Headers.EnumerateObject().Count()
            : -1;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != AcceptedStatus
            || response.BodyEnvelope is not null
            || headerCount != 0
            || !string.Equals(response.ResourceType, "outbox_message", StringComparison.Ordinal)
            || response.ResourceId != outcome.MessageId
            || outcome.ReplayOf != sourceMessageId)
        {
            throw new InvalidOperationException("The stored Outbox replay response is invalid.");
        }

        return Result.Success(outcome);
    }

    private static Result<OutboxReplayOutcome> ReplayFailure(
        CommandIdempotencyResponse response)
    {
        ReplayFailureBody failure = response.Body?.Deserialize<ReplayFailureBody>()
            ?? throw new InvalidOperationException("The Outbox replay failure is invalid.");
        ResultErrorPresentation expected = CreateFailurePresentation(
            failure.Presentation.Status,
            failure.Presentation.Code);
        int headerCount = response.Headers.ValueKind == JsonValueKind.Object
            ? response.Headers.EnumerateObject().Count()
            : -1;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Failed
            || response.Status != failure.Presentation.Status
            || response.BodyEnvelope is not null
            || headerCount != 0
            || response.ResourceType is not null
            || response.ResourceId is not null
            || failure.Presentation != expected)
        {
            throw new InvalidOperationException("The stored Outbox replay failure is invalid.");
        }

        return Failure(
            failure.Presentation.Code,
            failure.Description,
            presentation: failure.Presentation);
    }

    private async ValueTask<Result<OutboxReplayOutcome>> CompleteFailureAsync(
        CommandIdempotencyLease lease,
        int status,
        string code,
        string description,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        ResultErrorPresentation presentation = CreateFailurePresentation(status, code);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                status,
                JsonSerializer.SerializeToElement(new ReplayFailureBody(
                    description,
                    presentation)),
                ResponseBodyEnvelope: null,
                EmptyObject,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The Outbox replay idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure(code, description, presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync(
        CommandIdempotencyLease lease,
        OutboxReplayOutcome outcome,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                AcceptedStatus,
                JsonSerializer.SerializeToElement(OutboxReplayResponseSnapshot.From(outcome)),
                ResponseBodyEnvelope: null,
                EmptyObject,
                "outbox_message",
                outcome.MessageId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The Outbox replay idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        PreparedReplay prepared,
        OutboxReplayOutcome outcome,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken)
    {
        await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                AuditActorType.Admin,
                prepared.Command.Actor.UserId,
                "outbox.dead.replayed",
                "outbox_message",
                outcome.MessageId,
                prepared.Command.RequestId,
                prepared.Reason,
                prepared.Command.IpAddress,
                prepared.Command.UserAgent,
                JsonSerializer.SerializeToElement(new
                {
                    message_id = prepared.Command.SourceMessageId.Value,
                    status = "dead",
                }),
                JsonSerializer.SerializeToElement(new
                {
                    message_id = outcome.MessageId.Value,
                    event_sequence = outcome.EventSequence.ToString(CultureInfo.InvariantCulture),
                    replay_of = outcome.ReplayOf.Value,
                    status = PendingStatus,
                }),
                JsonSerializer.SerializeToElement(new
                {
                    operation = "replay_dead_outbox",
                    source_message_id = prepared.Command.SourceMessageId.Value,
                    replacement_message_id = outcome.MessageId.Value,
                    idempotency_key_hash = HmacText(
                        "poolai|audit-idempotency-key|operations-outbox-replay|v1\0",
                        prepared.Command.IdempotencyKey),
                })),
            context,
            cancellationToken).ConfigureAwait(false);
    }

    private byte[] HashRequest(EntityId sourceMessageId, string reason)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation = "replay_dead_outbox",
            source_message_id = sourceMessageId.Value,
            reason,
        });
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|operations-outbox-replay|v1\0");
        byte[] input = new byte[domain.Length + body.Length];
        try
        {
            domain.CopyTo(input, 0);
            body.CopyTo(input, domain.Length);
            return HMACSHA256.HashData(_policy.RequestHashPepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            CryptographicOperations.ZeroMemory(domain);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private string HmacText(string domain, string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(domain + value);
        try
        {
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_policy.RequestHashPepper, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static ResultErrorPresentation CreateFailurePresentation(
        int status,
        string code)
    {
        (string title, string detail) = (code, status) switch
        {
            (OperationsErrorCodes.ResourceNotFound, NotFoundStatus) =>
                ("Resource not found", "The requested resource was not found."),
            (OperationsErrorCodes.ResourceConflict, ConflictStatus) =>
                ("Resource conflict", "The requested state conflicts with the current resource state."),
            _ => throw new InvalidOperationException(
                "The Outbox replay failure code and status are unsupported."),
        };
        return new ResultErrorPresentation(code, status, title, detail, Retryable: false);
    }

    private static void ValidateOutcome(OutboxReplayOutcome outcome)
    {
        if (outcome.MessageId.Value == Guid.Empty
            || outcome.ReplayOf.Value == Guid.Empty
            || outcome.MessageId == outcome.ReplayOf
            || outcome.EventSequence <= 0)
        {
            throw new InvalidOperationException("The Outbox replay outcome is invalid.");
        }
    }

    private static string Scope(EntityId actorUserId, EntityId sourceMessageId) =>
        $"operations:{actorUserId.Value:D}:post:/api/v1/admin/outbox-messages/{sourceMessageId.Value:D}/replay";

    private static bool IsKnownRole(OperationsControlRole role) => role is
        OperationsControlRole.Admin or
        OperationsControlRole.Operator or
        OperationsControlRole.Auditor or
        OperationsControlRole.User;

    private static Result<OutboxReplayOutcome> Failure(
        string code,
        string description,
        long? retryAfterSeconds = null,
        ResultErrorPresentation? presentation = null) =>
        Result.Failure<OutboxReplayOutcome>(
            code,
            description,
            retryAfterSeconds,
            presentation: presentation);

    private sealed record PreparedReplay(
        ReplayDeadOutboxCommand Command,
        string Reason,
        string Scope,
        byte[] RequestHash);

    private sealed record ReplayFailureBody(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record OutboxReplayResponseSnapshot(
        Guid MessageId,
        string EventSequence,
        Guid ReplayOf,
        string Status)
    {
        internal static OutboxReplayResponseSnapshot From(OutboxReplayOutcome outcome) => new(
            outcome.MessageId.Value,
            outcome.EventSequence.ToString(CultureInfo.InvariantCulture),
            outcome.ReplayOf.Value,
            PendingStatus);

        internal OutboxReplayOutcome ToOutcome(bool isReplay)
        {
            if (!long.TryParse(
                    EventSequence,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long eventSequence)
                || eventSequence <= 0
                || !string.Equals(
                    EventSequence,
                    eventSequence.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                || !string.Equals(Status, PendingStatus, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The stored Outbox replay receipt is not canonical.");
            }

            return new OutboxReplayOutcome(
                isReplay,
                new EntityId(MessageId),
                eventSequence,
                new EntityId(ReplayOf));
        }
    }
}
#pragma warning restore MA0051
