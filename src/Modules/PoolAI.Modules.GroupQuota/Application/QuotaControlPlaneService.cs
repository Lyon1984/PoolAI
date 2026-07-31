#pragma warning disable MA0051 // Command handlers keep their complete transactional sequence visible.
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

internal sealed class QuotaControlPlaneService :
    IGetGroupQuotaUseCase,
    IAuthorizeQuotaMutationUseCase,
    IAdjustGroupQuotaUseCase,
    IResetGroupQuotaUseCase
{
    private const long MaximumSafeTokenCount = 9_007_199_254_740_991;
    private static readonly BigInteger MaximumSafeTokenCountValue =
        new(MaximumSafeTokenCount);
    private static readonly string[] QuotaStatusCodes =
        ["active", "exhausted", "disabled"];
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IQuotaRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly GroupQuotaPolicy _policy;

    internal QuotaControlPlaneService(
        IQuotaRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        GroupQuotaPolicy policy)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore
            ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async ValueTask<Result<GroupQuotaView>> ExecuteAsync(
        GetGroupQuotaQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<GroupQuotaView>(
                GroupErrorCodes.RoleRequired,
                "The actor role cannot read Group quota.");
        }

        if (query.GroupId.Value == Guid.Empty)
        {
            return Failure<GroupQuotaView>(
                GroupErrorCodes.InvalidRequest,
                "The Group quota query is invalid.");
        }

        GroupQuotaResource? quota = await _repository
            .GetCurrentAsync(query.GroupId, cancellationToken)
            .ConfigureAwait(false);
        return quota is null
            ? Failure<GroupQuotaView>(
                GroupErrorCodes.ResourceNotFound,
                "The Group quota does not exist.")
            : Result.Success(ToView(quota));
    }

    public ValueTask<Result<bool>> ExecuteAsync(
        AuthorizeQuotaMutationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return AuthorizeMutationAsync(
            command.RequestId,
            command.Actor,
            command.GroupId,
            command.Operation,
            command.IpAddress,
            command.UserAgent,
            cancellationToken);
    }

    public async ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
        AdjustGroupQuotaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Result<bool> authorization = await AuthorizeMutationAsync(
            command.RequestId,
            command.Actor,
            command.GroupId,
            QuotaMutationOperation.AdjustTotal,
            command.IpAddress,
            command.UserAgent,
            cancellationToken).ConfigureAwait(false);
        if (authorization.IsFailure)
        {
            return CopyFailure<GroupQuotaCommandOutcome>(authorization.Error);
        }

        PreparedQuotaMutation prepared;
        try
        {
            prepared = Prepare(
                QuotaMutationKind.Adjust,
                command.RequestId,
                command.Actor,
                command.IdempotencyKey,
                command.GroupId,
                command.ExpectedVersion,
                command.NewTotalTokens,
                command.Reason,
                command.IpAddress,
                command.UserAgent);
        }
        catch (ArgumentException)
        {
            return Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.ValidationFailed,
                "The quota-adjust request is invalid.");
        }

        return await ExecuteMutationAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteAsync(
        ResetGroupQuotaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Result<bool> authorization = await AuthorizeMutationAsync(
            command.RequestId,
            command.Actor,
            command.GroupId,
            QuotaMutationOperation.ResetPeriod,
            command.IpAddress,
            command.UserAgent,
            cancellationToken).ConfigureAwait(false);
        if (authorization.IsFailure)
        {
            return CopyFailure<GroupQuotaCommandOutcome>(authorization.Error);
        }

        PreparedQuotaMutation prepared;
        try
        {
            prepared = Prepare(
                QuotaMutationKind.Reset,
                command.RequestId,
                command.Actor,
                command.IdempotencyKey,
                command.GroupId,
                command.ExpectedVersion,
                command.TotalTokens,
                command.Reason,
                command.IpAddress,
                command.UserAgent);
        }
        catch (ArgumentException)
        {
            return Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.ValidationFailed,
                "The quota-reset request is invalid.");
        }

        return await ExecuteMutationAsync(prepared, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<GroupQuotaCommandOutcome>> ExecuteMutationAsync(
        PreparedQuotaMutation prepared,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                prepared.Scope,
                prepared.IdempotencyKey,
                EntityId.New(),
                $"user:{prepared.Actor.UserId.Value:D}",
                prepared.RequestHash,
                prepared.RequestId,
                IdempotencyLease,
                IdempotencyRetention),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<GroupQuotaCommandOutcome>? early = ReplayOrAcquireFailure(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        EntityId eventId = EntityId.New();
        EntityId outboxId = EntityId.New();
        QuotaWriteResult write = prepared.Kind.IsAdjust
            ? await _repository.AdjustTotalAsync(
                new AdjustQuotaWrite(
                    prepared.GroupId,
                    prepared.TotalTokens,
                    prepared.ExpectedVersion,
                    prepared.Actor.UserId,
                    eventId,
                    outboxId,
                    prepared.EventIdempotencyKey,
                    prepared.Reason),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false)
            : await _repository.ResetAsync(
                new ResetQuotaWrite(
                    prepared.GroupId,
                    EntityId.New(),
                    prepared.TotalTokens,
                    prepared.ExpectedVersion,
                    prepared.Actor.UserId,
                    eventId,
                    outboxId,
                    prepared.EventIdempotencyKey,
                    prepared.Reason),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);

        if (write.Disposition != QuotaWriteDisposition.Written)
        {
            return await CompleteWriteFailureAsync(
                lease,
                write,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        GroupQuotaResource before = write.Before
            ?? throw new InvalidOperationException(
                "The quota mutation did not return its canonical before-state.");
        GroupQuotaResource after = write.After
            ?? throw new InvalidOperationException(
                "The quota mutation did not return its canonical after-state.");
        ValidateResource(before);
        ValidateResource(after);
        await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                AuditActorType.Admin,
                prepared.Actor.UserId,
                prepared.Kind.AuditAction,
                "group_quota",
                prepared.GroupId,
                prepared.RequestId,
                prepared.Reason,
                prepared.IpAddress,
                prepared.UserAgent,
                AuditState(before),
                AuditState(after),
                JsonSerializer.SerializeToElement(new
                {
                    operation = prepared.Kind.OperationCode,
                    idempotency_key_hash = HmacText(
                        "poolai|audit-idempotency-key|groupquota-period|v1\0",
                        prepared.IdempotencyKey),
                })),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        GroupQuotaView view = ToView(after);
        string etag = ETag(view.Version);
        await CompleteSuccessAsync(
            lease,
            view,
            etag,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new GroupQuotaCommandOutcome(200, false, view, etag));
    }

    private async ValueTask<Result<GroupQuotaCommandOutcome>> CompleteWriteFailureAsync(
        CommandIdempotencyLease lease,
        QuotaWriteResult write,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        return write.Disposition switch
        {
            QuotaWriteDisposition.NotFound =>
                await CompleteFailureAsync(
                    lease,
                    404,
                    GroupErrorCodes.ResourceNotFound,
                    "The Group quota does not exist.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            QuotaWriteDisposition.VersionConflict =>
                await CompleteFailureAsync(
                    lease,
                    412,
                    GroupErrorCodes.VersionConflict,
                    "The Group quota version has changed.",
                    unitOfWork,
                    cancellationToken,
                    ETag(write.CurrentVersion
                        ?? throw new InvalidOperationException(
                            "A quota version conflict did not return the current version.")))
                    .ConfigureAwait(false),
            QuotaWriteDisposition.Archived =>
                await CompleteFailureAsync(
                    lease,
                    409,
                    GroupErrorCodes.ResourceConflict,
                    "An archived Group cannot accept quota mutations.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            QuotaWriteDisposition.IdempotencyConflict =>
                await CompleteFailureAsync(
                    lease,
                    409,
                    GroupErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used for a different quota event.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            QuotaWriteDisposition.Conflict =>
                await CompleteFailureAsync(
                    lease,
                    409,
                    GroupErrorCodes.ResourceConflict,
                    "The Group quota is not in a mutable current-period state.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                "The quota repository returned an invalid write disposition."),
        };
    }

    private PreparedQuotaMutation Prepare(
        QuotaMutationKind kind,
        EntityId requestId,
        GroupActor actor,
        string idempotencyKey,
        EntityId groupId,
        long expectedVersion,
        long totalTokens,
        string reason,
        string? ipAddress,
        string? userAgent)
    {
        GroupInput.IdempotencyKey(idempotencyKey);
        string normalizedReason = QuotaReason(reason);
        if (expectedVersion <= 0
            || totalTokens is < 1 or > MaximumSafeTokenCount)
        {
            throw new ArgumentException(
                "The quota mutation is incomplete.",
                nameof(groupId));
        }

        string scope = Scope(kind, actor.UserId, groupId);
        byte[] requestHash = HashRequest(new
        {
            operation = kind.OperationCode,
            group_id = groupId.Value,
            expected_version = expectedVersion,
            total_tokens = totalTokens,
            reason = normalizedReason,
        });
        return new PreparedQuotaMutation(
            kind,
            requestId,
            actor,
            idempotencyKey,
            groupId,
            expectedVersion,
            new BigInteger(totalTokens),
            normalizedReason,
            ipAddress,
            userAgent,
            scope,
            requestHash,
            $"{kind.OperationCode}:{HmacText(
                "poolai|quota-event-idempotency-key|groupquota-period|v1\0",
                scope + "\0" + idempotencyKey)}");
    }

    private async ValueTask<Result<bool>> AuthorizeMutationAsync(
        EntityId requestId,
        GroupActor actor,
        EntityId groupId,
        QuotaMutationOperation operation,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (requestId.Value == Guid.Empty
            || actor.UserId.Value == Guid.Empty
            || actor.TokenVersion <= 0
            || !IsKnownRole(actor.Role)
            || groupId.Value == Guid.Empty
            || operation is not QuotaMutationOperation.AdjustTotal
                and not QuotaMutationOperation.ResetPeriod)
        {
            return Failure<bool>(
                GroupErrorCodes.InvalidRequest,
                "The quota-mutation authorization request is invalid.");
        }

        if (actor.Role == GroupControlRole.Admin)
        {
            return Result.Success(true);
        }

        // The known-role guard and Admin fast path above prove this is exactly
        // one of Operator, Auditor, or User.
        AuditActorType deniedActorType = ToDeniedAuditActorType(actor.Role);
        bool adjust = operation == QuotaMutationOperation.AdjustTotal;
        await AppendDeniedAuditAsync(
            actor,
            deniedActorType,
            groupId,
            requestId,
            adjust
                ? "groupquota.quota.total_adjust_denied"
                : "groupquota.quota.period_reset_denied",
            adjust ? "adjust_total" : "reset_period",
            ipAddress,
            userAgent,
            cancellationToken).ConfigureAwait(false);
        return Failure<bool>(
            GroupErrorCodes.RoleRequired,
            "The admin role is required.");
    }

    private async ValueTask AppendDeniedAuditAsync(
        GroupActor actor,
        AuditActorType actorType,
        EntityId groupId,
        EntityId requestId,
        string action,
        string operation,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                actorType,
                actor.UserId,
                action,
                "group_quota",
                groupId,
                requestId,
                Reason: null,
                ipAddress,
                userAgent,
                BeforeState: null,
                AfterState: null,
                JsonSerializer.SerializeToElement(new
                {
                    operation,
                    denial_code = GroupErrorCodes.RoleRequired,
                })),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Result<GroupQuotaCommandOutcome>? ReplayOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure<GroupQuotaCommandOutcome>(
                GroupErrorCodes.CoordinationUnavailable,
                "The matching idempotent command is still in progress.",
                retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => Replay(acquire.Response!),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<GroupQuotaCommandOutcome> Replay(
        CommandIdempotencyResponse response)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailure(response);
        }

        GroupQuotaViewReplay replay = response.Body?.Deserialize<GroupQuotaViewReplay>()
            ?? throw new InvalidOperationException("The quota success replay body is invalid.");
        GroupQuotaView view = replay.ToView();
        ValidateView(view);
        string? etag = Header(response.Headers, "ETag");
        int headerCount = response.Headers.ValueKind == JsonValueKind.Object
            ? response.Headers.EnumerateObject().Count()
            : -1;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 200
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "group_quota", StringComparison.Ordinal)
            || response.ResourceId != view.GroupId
            || !string.Equals(etag, ETag(view.Version), StringComparison.Ordinal)
            || headerCount != 1)
        {
            throw new InvalidOperationException("The quota success replay is invalid.");
        }

        return Result.Success(new GroupQuotaCommandOutcome(200, true, view, etag!));
    }

    private static Result<GroupQuotaCommandOutcome> ReplayFailure(
        CommandIdempotencyResponse response)
    {
        ReplayFailureBody failure = response.Body?.Deserialize<ReplayFailureBody>()
            ?? throw new InvalidOperationException("The quota failure replay body is invalid.");
        string? etag = Header(response.Headers, "ETag");
        int headerCount = response.Headers.ValueKind == JsonValueKind.Object
            ? response.Headers.EnumerateObject().Count()
            : -1;
        ResultErrorPresentation expected = CreateFailurePresentation(
            failure.Presentation.Status,
            failure.Presentation.Code);
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Failed
            || response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || response.Status != failure.Presentation.Status
            || failure.Presentation != expected
            || (response.Status == 412) != (etag is not null)
            || etag is not null && !IsCanonicalETag(etag)
            || headerCount != (etag is null ? 0 : 1))
        {
            throw new InvalidOperationException("The quota failure replay is invalid.");
        }

        return Failure<GroupQuotaCommandOutcome>(
            failure.Presentation.Code,
            failure.Description,
            etag: etag,
            presentation: failure.Presentation);
    }

    private async ValueTask<Result<GroupQuotaCommandOutcome>> CompleteFailureAsync(
        CommandIdempotencyLease lease,
        int status,
        string code,
        string description,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken,
        string? etag = null)
    {
        ResultErrorPresentation presentation = CreateFailurePresentation(status, code);
        JsonElement headers = etag is null
            ? EmptyObject
            : JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ETag"] = etag,
                });
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                status,
                JsonSerializer.SerializeToElement(new ReplayFailureBody(description, presentation)),
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The quota idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<GroupQuotaCommandOutcome>(
            code,
            description,
            etag: etag,
            presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync(
        CommandIdempotencyLease lease,
        GroupQuotaView view,
        string etag,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement headers = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            });
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                200,
                JsonSerializer.SerializeToElement(GroupQuotaViewReplay.From(view)),
                ResponseBodyEnvelope: null,
                headers,
                "group_quota",
                view.GroupId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The quota idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ResultErrorPresentation CreateFailurePresentation(
        int status,
        string code)
    {
        (string title, string detail, bool retryable) = (code, status) switch
        {
            (GroupErrorCodes.ResourceNotFound, 404) =>
                ("Resource not found", "The requested resource was not found.", false),
            (GroupErrorCodes.ResourceConflict, 409) =>
                ("Resource conflict", "The requested state conflicts with the current resource state.", false),
            (GroupErrorCodes.IdempotencyConflict, 409) =>
                ("Idempotency conflict", "The idempotency key was used for a different request.", false),
            (GroupErrorCodes.VersionConflict, 412) =>
                ("Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
            _ => throw new InvalidOperationException(
                "The quota idempotent failure code and status are unsupported."),
        };
        return new ResultErrorPresentation(code, status, title, detail, retryable);
    }

    private static JsonElement AuditState(GroupQuotaResource quota) =>
        JsonSerializer.SerializeToElement(new
        {
            group_id = quota.GroupId.Value,
            period_id = quota.PeriodId.Value,
            status = StatusCode(quota.Status),
            total_tokens = quota.TotalTokens.ToString(CultureInfo.InvariantCulture),
            consumed_tokens = quota.ConsumedTokens.ToString(CultureInfo.InvariantCulture),
            reserved_tokens = quota.ReservedTokens.ToString(CultureInfo.InvariantCulture),
            remaining_tokens = quota.RemainingTokens.ToString(CultureInfo.InvariantCulture),
            overage_tokens = quota.OverageTokens.ToString(CultureInfo.InvariantCulture),
            period_started_at = quota.PeriodStartedAt,
            period_ended_at = quota.PeriodEndedAt,
            version = quota.Version,
            updated_at = quota.UpdatedAt,
        });

    private static GroupQuotaView ToView(GroupQuotaResource resource)
    {
        ValidateResource(resource);
        return new GroupQuotaView(
            resource.GroupId,
            resource.PeriodId,
            resource.Status,
            resource.TotalTokens,
            resource.ConsumedTokens,
            resource.ReservedTokens,
            resource.RemainingTokens,
            resource.OverageTokens,
            resource.PeriodStartedAt,
            resource.PeriodEndedAt,
            resource.Version,
            resource.UpdatedAt);
    }

    private static void ValidateResource(GroupQuotaResource resource) =>
        ValidateView(new GroupQuotaView(
            resource.GroupId,
            resource.PeriodId,
            resource.Status,
            resource.TotalTokens,
            resource.ConsumedTokens,
            resource.ReservedTokens,
            resource.RemainingTokens,
            resource.OverageTokens,
            resource.PeriodStartedAt,
            resource.PeriodEndedAt,
            resource.Version,
            resource.UpdatedAt));

    private static void ValidateView(GroupQuotaView view)
    {
        BigInteger remaining = BigInteger.Max(
            view.TotalTokens - view.ConsumedTokens - view.ReservedTokens,
            BigInteger.Zero);
        BigInteger overage = BigInteger.Max(
            view.ConsumedTokens - view.TotalTokens,
            BigInteger.Zero);
        bool statusValid = view.Status switch
        {
            GroupPoolQuotaStatus.Active => view.ConsumedTokens < view.TotalTokens,
            GroupPoolQuotaStatus.Exhausted => view.ConsumedTokens >= view.TotalTokens,
            GroupPoolQuotaStatus.Disabled => true,
            _ => false,
        };
        if (view.GroupId.Value == Guid.Empty
            || view.PeriodId.Value == Guid.Empty
            || view.TotalTokens <= BigInteger.Zero
            || view.TotalTokens > MaximumSafeTokenCountValue
            || view.ConsumedTokens < BigInteger.Zero
            || view.ReservedTokens < BigInteger.Zero
            || view.RemainingTokens != remaining
            || view.OverageTokens != overage
            || view.PeriodEndedAt is not null
            || view.Version <= 0
            || view.UpdatedAt < view.PeriodStartedAt
            || !statusValid)
        {
            throw new InvalidOperationException("The canonical Group quota snapshot is invalid.");
        }
    }

    private byte[] HashRequest<T>(T request)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|groupquota-period|v1\0");
        byte[] input = new byte[domain.Length + bytes.Length];
        try
        {
            domain.CopyTo(input, 0);
            bytes.CopyTo(input, domain.Length);
            return HMACSHA256.HashData(_policy.RequestHashPepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private string HmacText(string domain, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(domain + value);
        try
        {
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_policy.RequestHashPepper, bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static AuditActorType ToDeniedAuditActorType(GroupControlRole role) =>
        role == GroupControlRole.Operator
            ? AuditActorType.Operator
            : role == GroupControlRole.Auditor
                ? AuditActorType.Auditor
                : AuditActorType.User;

    private static string Scope(
        QuotaMutationKind kind,
        EntityId actorUserId,
        EntityId groupId) =>
        $"groupquota:{actorUserId.Value:D}:post:/api/v1/admin/groups/{groupId.Value:D}/quota/{kind.ScopeSuffix}";

    private static bool IsKnownRole(GroupControlRole role) =>
        role is GroupControlRole.Admin
            or GroupControlRole.Operator
            or GroupControlRole.Auditor
            or GroupControlRole.User;

    private static bool CanRead(GroupActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is GroupControlRole.Admin
            or GroupControlRole.Operator
            or GroupControlRole.Auditor;

    // Every caller first validates the canonical view, so the enum ordinal is
    // already proven to be one of the complete three-value contract set.
    private static string StatusCode(GroupPoolQuotaStatus status) =>
        QuotaStatusCodes[(int)status];

    private static GroupPoolQuotaStatus ParseStatus(string status) => status switch
    {
        "active" => GroupPoolQuotaStatus.Active,
        "exhausted" => GroupPoolQuotaStatus.Exhausted,
        "disabled" => GroupPoolQuotaStatus.Disabled,
        _ => throw new InvalidOperationException("The quota replay status is invalid."),
    };

    private static string ETag(long version) => $"\"v{version}\"";

    private static bool IsCanonicalETag(string etag) =>
        etag.Length >= 4
        && etag[0] == '"'
        && etag[1] == 'v'
        && etag[2] is >= '1' and <= '9'
        && etag[^1] == '"'
        && long.TryParse(
            etag.AsSpan(2, etag.Length - 3),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
            && headers.TryGetProperty(name, out JsonElement value)
                ? value.GetString()
                : null;

    private static BigInteger ParseCanonicalTokenCount(string value)
    {
        if (value.Length is < 1 or > 78
            || !string.Equals(value, "0", StringComparison.Ordinal)
                && (value[0] is < '1' or > '9'
                    || value.AsSpan(1).ContainsAnyExceptInRange('0', '9'))
            || !BigInteger.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out BigInteger parsed)
            || parsed < BigInteger.Zero)
        {
            throw new InvalidOperationException(
                "The quota replay Token count is not canonical.");
        }

        return parsed;
    }

    private static string QuotaReason(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A quota reason must contain a non-whitespace character.",
                nameof(value));
        }

        int scalarCount = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(
                    remaining,
                    out _,
                    out int consumed) != OperationStatus.Done)
            {
                throw new ArgumentException(
                    "A quota reason must contain valid Unicode scalar values.",
                    nameof(value));
            }

            scalarCount++;
            if (scalarCount > 500)
            {
                throw new ArgumentException(
                    "A quota reason cannot exceed 500 Unicode scalar values.",
                    nameof(value));
            }

            remaining = remaining[consumed..];
        }

        return value.Trim();
    }

    private static Result<T> CopyFailure<T>(ResultError error) => Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        string? etag = null,
        ResultErrorPresentation? presentation = null) => Result.Failure<T>(
            code,
            description,
            retryAfterSeconds,
            etag,
            presentation);

    private sealed class QuotaMutationKind
    {
        private QuotaMutationKind(
            bool isAdjust,
            string operationCode,
            string scopeSuffix,
            string auditAction)
        {
            IsAdjust = isAdjust;
            OperationCode = operationCode;
            ScopeSuffix = scopeSuffix;
            AuditAction = auditAction;
        }

        internal static QuotaMutationKind Adjust { get; } = new(
            isAdjust: true,
            "total_adjusted",
            "adjust",
            "groupquota.quota.total_adjusted");

        internal static QuotaMutationKind Reset { get; } = new(
            isAdjust: false,
            "period_reset",
            "reset",
            "groupquota.quota.period_reset");

        internal bool IsAdjust { get; }

        internal string OperationCode { get; }

        internal string ScopeSuffix { get; }

        internal string AuditAction { get; }
    }

    private sealed record PreparedQuotaMutation(
        QuotaMutationKind Kind,
        EntityId RequestId,
        GroupActor Actor,
        string IdempotencyKey,
        EntityId GroupId,
        long ExpectedVersion,
        BigInteger TotalTokens,
        string Reason,
        string? IpAddress,
        string? UserAgent,
        string Scope,
        byte[] RequestHash,
        string EventIdempotencyKey);

    private sealed record ReplayFailureBody(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record GroupQuotaViewReplay(
        Guid GroupId,
        Guid PeriodId,
        string Status,
        string TotalTokens,
        string ConsumedTokens,
        string ReservedTokens,
        string RemainingTokens,
        string OverageTokens,
        DateTimeOffset PeriodStartedAt,
        DateTimeOffset? PeriodEndedAt,
        long Version,
        DateTimeOffset UpdatedAt)
    {
        internal static GroupQuotaViewReplay From(GroupQuotaView view) => new(
            view.GroupId.Value,
            view.PeriodId.Value,
            StatusCode(view.Status),
            view.TotalTokens.ToString(CultureInfo.InvariantCulture),
            view.ConsumedTokens.ToString(CultureInfo.InvariantCulture),
            view.ReservedTokens.ToString(CultureInfo.InvariantCulture),
            view.RemainingTokens.ToString(CultureInfo.InvariantCulture),
            view.OverageTokens.ToString(CultureInfo.InvariantCulture),
            view.PeriodStartedAt,
            view.PeriodEndedAt,
            view.Version,
            view.UpdatedAt);

        internal GroupQuotaView ToView() => new(
            new EntityId(GroupId),
            new EntityId(PeriodId),
            ParseStatus(Status),
            ParseCanonicalTokenCount(TotalTokens),
            ParseCanonicalTokenCount(ConsumedTokens),
            ParseCanonicalTokenCount(ReservedTokens),
            ParseCanonicalTokenCount(RemainingTokens),
            ParseCanonicalTokenCount(OverageTokens),
            PeriodStartedAt,
            PeriodEndedAt,
            Version,
            UpdatedAt);
    }
}
#pragma warning restore MA0051
