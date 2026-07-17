#pragma warning disable MA0051 // Command handlers keep their complete transactional sequence visible.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.GroupQuota.Application.Ports;
using PoolAI.Modules.GroupQuota.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

internal sealed class GroupControlPlaneService :
    IListGroupsUseCase,
    IGetGroupUseCase,
    ICreateGroupUseCase,
    IUpdateGroupUseCase,
    IGroupStatusReader,
    IGroupActivationIdempotencyPreflight,
    IGroupActivationCommand
{
    private const string EventTopic = "poolai.group.v1";
    private const int EventSchemaVersion = 1;
    private const long MaximumSafeTokenCount = 9_007_199_254_740_991;
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IGroupRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IOutboxAppender _outboxAppender;
    private readonly GroupQuotaPolicy _policy;
    private readonly TimeProvider _timeProvider;

    internal GroupControlPlaneService(
        IGroupRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IOutboxAppender outboxAppender,
        GroupQuotaPolicy policy,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _outboxAppender = outboxAppender ?? throw new ArgumentNullException(nameof(outboxAppender));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<Result<GroupPage>> ExecuteAsync(
        ListGroupsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<GroupPage>(
                GroupErrorCodes.RoleRequired,
                "The actor role cannot read Groups.");
        }

        if (query.Limit is < 1 or > 100
            || !TryDecodeCursor(query.Cursor, out GroupCursor? cursor))
        {
            return Failure<GroupPage>(
                GroupErrorCodes.InvalidRequest,
                "The Group pagination request is invalid.");
        }

        GroupSlice slice = await _repository
            .ListAsync(cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        GroupView[] groups = slice.Groups.Select(ToView).ToArray();
        string? nextCursor = slice.HasMore && slice.Groups.Count > 0
            ? EncodeCursor(slice.Groups[^1])
            : null;
        return Result.Success(new GroupPage(groups, nextCursor, slice.HasMore));
    }

    public async ValueTask<Result<GroupView>> ExecuteAsync(
        GetGroupQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanRead(query.Actor))
        {
            return Failure<GroupView>(
                GroupErrorCodes.RoleRequired,
                "The actor role cannot read Groups.");
        }

        GroupResource? group = await _repository
            .GetAsync(query.GroupId, cancellationToken)
            .ConfigureAwait(false);
        return group is null
            ? Failure<GroupView>(
                GroupErrorCodes.ResourceNotFound,
                "The Group does not exist.")
            : Result.Success(ToView(group));
    }

    public async ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
        CreateGroupCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdmin(command.Actor))
        {
            return Failure<GroupCommandOutcome>(
                GroupErrorCodes.RoleRequired,
                "The admin role is required.");
        }

        string name;
        string? description;
        byte[] requestHash;
        try
        {
            GroupInput.IdempotencyKey(command.IdempotencyKey);
            name = GroupInput.Name(command.Name);
            description = GroupInput.Description(command.Description);
            if (command.TotalTokens is < 1 or > MaximumSafeTokenCount)
            {
                throw new ArgumentOutOfRangeException(nameof(command));
            }

            requestHash = HashRequest(new
            {
                name,
                description,
                total_tokens = command.TotalTokens,
            });
        }
        catch (ArgumentException)
        {
            return Failure<GroupCommandOutcome>(
                GroupErrorCodes.ValidationFailed,
                "The create-Group request is invalid.");
        }

        string scope = CreateScope(command.Actor);
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            scope,
            command.IdempotencyKey,
            command.Actor.UserId,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<GroupCommandOutcome>? early = ReplayOrAcquireFailure(acquire, expectedStatus: 201);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        EntityId groupId = EntityId.New();
        GroupWriteResult create = await _repository.CreateAsync(
            new CreateGroupWrite(
                groupId,
                EntityId.New(),
                EntityId.New(),
                EntityId.New(),
                name,
                description,
                command.TotalTokens,
                command.Actor.UserId,
                QuotaIdempotencyKey(scope, command.IdempotencyKey),
                "Initial quota provisioned with Group creation."),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (create.Disposition != GroupWriteDisposition.Written)
        {
            return await CompleteFailureAsync<GroupCommandOutcome>(
                lease,
                409,
                GroupErrorCodes.ResourceConflict,
                create.Disposition == GroupWriteDisposition.NameConflict
                    ? "The Group name already exists."
                    : "The Group and initial quota could not be created in the requested state.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        GroupResource group = create.Group!;
        await AppendAuditAsync(
            command.Actor.UserId,
            "groupquota.group.created",
            group.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            before: null,
            after: AuditState(group),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "group_created",
            group,
            command.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = EventSchemaVersion,
                event_type = "group_created",
                group_id = group.Id.Value,
                name = group.Name,
                status = LifecycleCode(group.Lifecycle),
                version = group.Version,
                origin = "admin_api",
            }),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        GroupView view = ToView(group);
        string etag = ETag(view.Version);
        string location = $"/api/v1/admin/groups/{view.Id.Value:D}";
        await CompleteSuccessAsync(
            lease,
            201,
            view,
            etag,
            location,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new GroupCommandOutcome(201, false, view, etag, location));
    }

    public async ValueTask<Result<GroupCommandOutcome>> ExecuteAsync(
        UpdateGroupCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdmin(command.Actor))
        {
            return Failure<GroupCommandOutcome>(
                GroupErrorCodes.RoleRequired,
                "The admin role is required.");
        }

        if (command.HasStatus && command.Status == GroupLifecycle.Active)
        {
            return Failure<GroupCommandOutcome>(
                GroupErrorCodes.GroupActivationNotReady,
                "Activation must be executed by the Group activation orchestrator.");
        }

        string? normalizedName;
        string? normalizedDescription;
        string? normalizedReason;
        byte[] requestHash;
        try
        {
            GroupInput.IdempotencyKey(command.IdempotencyKey);
            if (command.ExpectedVersion <= 0
                || (!command.HasName && !command.HasDescription && !command.HasStatus)
                || command.HasName && command.Name is null
                || command.HasStatus && command.Status is null)
            {
                throw new ArgumentException("The Group update is incomplete.", nameof(command));
            }

            normalizedName = command.HasName ? GroupInput.Name(command.Name!) : null;
            normalizedDescription = command.HasDescription
                ? GroupInput.Description(command.Description)
                : null;
            normalizedReason = command.HasStatus
                ? GroupInput.Reason(command.Reason ?? string.Empty)
                : command.Reason is null ? null : GroupInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                group_id = command.GroupId.Value,
                expected_version = command.ExpectedVersion,
                has_name = command.HasName,
                name = normalizedName,
                has_description = command.HasDescription,
                description = normalizedDescription,
                has_status = command.HasStatus,
                status = command.Status is null ? null : LifecycleCode(command.Status.Value),
                reason = normalizedReason,
            });
        }
        catch (ArgumentException)
        {
            return Failure<GroupCommandOutcome>(
                GroupErrorCodes.ValidationFailed,
                "The update-Group request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor.UserId, command.GroupId),
            command.IdempotencyKey,
            command.Actor.UserId,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<GroupCommandOutcome>? early = ReplayOrAcquireFailure(acquire, expectedStatus: 200);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        GroupWriteResult update = await _repository.UpdateAsync(
            new UpdateGroupWrite(
                command.GroupId,
                command.ExpectedVersion,
                command.HasName,
                normalizedName,
                command.HasDescription,
                normalizedDescription,
                command.HasStatus ? command.Status : null,
                command.HasStatus ? normalizedReason : null,
                SupplyEvidence: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (update.Disposition != GroupWriteDisposition.Written)
        {
            if (update.Disposition == GroupWriteDisposition.NotFound)
            {
                return await CompleteFailureAsync<GroupCommandOutcome>(
                    lease,
                    404,
                    GroupErrorCodes.ResourceNotFound,
                    "The Group does not exist.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            if (update.Disposition == GroupWriteDisposition.VersionConflict)
            {
                long version = update.CurrentVersion
                    ?? throw new InvalidOperationException(
                        "A Group version conflict did not return the current version.");
                return await CompleteFailureAsync<GroupCommandOutcome>(
                    lease,
                    412,
                    GroupErrorCodes.VersionConflict,
                    "The Group version has changed.",
                    unitOfWork,
                    cancellationToken,
                    ETag(version)).ConfigureAwait(false);
            }

            string description = update.Disposition switch
            {
                GroupWriteDisposition.NameConflict => "The Group name already exists.",
                GroupWriteDisposition.ArchiveBlocked =>
                    "The Group still has a pending reservation or an active or scheduled Subscription.",
                _ => "The requested Group lifecycle transition is not allowed.",
            };
            return await CompleteFailureAsync<GroupCommandOutcome>(
                lease,
                409,
                GroupErrorCodes.ResourceConflict,
                description,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        GroupResource updated = update.Group!;
        GroupResource before = update.Before
            ?? throw new InvalidOperationException(
                "The Group update function did not return its canonical before-state.");
        if (update.WasChanged)
        {
            string[] changedFields = ChangedFields(before, updated);
            await AppendAuditAsync(
                command.Actor.UserId,
                "groupquota.group.updated",
                updated.Id,
                command.RequestId,
                normalizedReason,
                command.IpAddress,
                command.UserAgent,
                AuditState(before),
                AuditState(updated),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await AppendEventAsync(
                "group_updated",
                updated,
                command.RequestId,
                JsonSerializer.SerializeToElement(new
                {
                    schema_version = EventSchemaVersion,
                    event_type = "group_updated",
                    group_id = updated.Id.Value,
                    status = LifecycleCode(updated.Lifecycle),
                    version = updated.Version,
                    changed_fields = changedFields,
                    origin = "admin_api",
                }),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        GroupView view = ToView(updated);
        string etag = ETag(view.Version);
        await CompleteSuccessAsync(
            lease,
            200,
            view,
            etag,
            location: null,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new GroupCommandOutcome(200, false, view, etag));
    }

    public async ValueTask<Result<GroupSnapshot>> GetAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        GroupResource? group = await _repository
            .GetAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        return group is null
            ? Failure<GroupSnapshot>(
                GroupErrorCodes.ResourceNotFound,
                "The Group does not exist.")
            : Result.Success(new GroupSnapshot(
                group.Id,
                group.Lifecycle,
                group.Version,
                group.HasCurrentQuotaPeriod,
                group.ObservedAt));
    }

    public async ValueTask<Result<GroupActivationResult>> ActivateAsync(
        ActivateGroupCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedActivation prepared;
        try
        {
            if (command.SupplyEvidence is not null
                && !IsValidEvidence(command.SupplyEvidence))
            {
                throw new ArgumentException("The Group activation is invalid.", nameof(command));
            }

            prepared = PrepareActivation(
                command.GroupId,
                command.ExpectedVersion,
                command.IdempotencyKey,
                command.Reason,
                command.MetadataPatch);
        }
        catch (ArgumentException)
        {
            return Failure<GroupActivationResult>(
                GroupErrorCodes.ValidationFailed,
                "The Group activation request is invalid.");
        }

        EntityId requestId = command.RequestId ?? EntityId.New();
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor.UserId, command.GroupId),
            command.IdempotencyKey,
            command.Actor.UserId,
            prepared.RequestHash,
            requestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<GroupActivationResult>? early = ReplayActivationOrAcquireFailure(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        GroupResource? current = await _repository.GetForActivationAsync(
            command.GroupId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<GroupActivationResult>? preconditionFailure = await CompleteActivationPreconditionFailureAsync(
            command,
            current,
            lease,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        if (preconditionFailure is not null)
        {
            return preconditionFailure;
        }

        GroupWriteResult activation = await _repository.UpdateAsync(
            new UpdateGroupWrite(
                command.GroupId,
                command.ExpectedVersion,
                prepared.HasName,
                prepared.Name,
                prepared.HasDescription,
                prepared.Description,
                GroupLifecycle.Active,
                prepared.Reason,
                command.SupplyEvidence),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (activation.Disposition != GroupWriteDisposition.Written)
        {
            if (activation.Disposition == GroupWriteDisposition.NotFound)
            {
                return await CompleteFailureAsync<GroupActivationResult>(
                    lease,
                    404,
                    GroupErrorCodes.ResourceNotFound,
                    "The Group does not exist.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            if (activation.Disposition == GroupWriteDisposition.VersionConflict)
            {
                long version = activation.CurrentVersion
                    ?? throw new InvalidOperationException(
                        "A Group version conflict did not return the current version.");
                return await CompleteFailureAsync<GroupActivationResult>(
                    lease,
                    412,
                    GroupErrorCodes.VersionConflict,
                    "The Group version has changed.",
                    unitOfWork,
                    cancellationToken,
                    ETag(version)).ConfigureAwait(false);
            }

            (string code, string description) = activation.Disposition switch
            {
                GroupWriteDisposition.NameConflict => (
                    GroupErrorCodes.ResourceConflict,
                    "The Group name already exists."),
                _ => (
                    GroupErrorCodes.GroupActivationNotReady,
                    "The Group no longer satisfies its activation preconditions."),
            };
            return await CompleteFailureAsync<GroupActivationResult>(
                lease,
                409,
                code,
                description,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        GroupResource updated = activation.Group!;
        SupplyReadinessEvidence supplyEvidence = command.SupplyEvidence
            ?? throw new InvalidOperationException(
                "The database allowed activation without Supply readiness evidence.");
        GroupResource before = activation.Before
            ?? throw new InvalidOperationException(
                "The Group activation function did not return its canonical before-state.");
        await AppendAuditAsync(
            command.Actor.UserId,
            "groupquota.group.activated",
            updated.Id,
            requestId,
            prepared.Reason,
            command.IpAddress,
            command.UserAgent,
            AuditState(before),
            AuditState(updated),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await AppendEventAsync(
            "group_activated",
            updated,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = EventSchemaVersion,
                event_type = "group_activated",
                group_id = updated.Id.Value,
                status = LifecycleCode(updated.Lifecycle),
                version = updated.Version,
                supply_readiness_token = supplyEvidence.OpaqueToken,
                supply_observed_at = supplyEvidence.ObservedAt,
                changed_fields = ChangedFields(before, updated),
                origin = "admin_api",
            }),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        GroupView view = ToView(updated);
        string etag = ETag(view.Version);
        await CompleteSuccessAsync(
            lease,
            200,
            view,
            etag,
            location: null,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new GroupActivationResult(
            updated.Id,
            updated.Lifecycle,
            updated.Version,
            updated.ToSnapshot()));
    }

    private async ValueTask<Result<GroupActivationResult>?> CompleteActivationPreconditionFailureAsync(
        ActivateGroupCommand command,
        GroupResource? current,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            return await CompleteFailureAsync<GroupActivationResult>(
                lease,
                404,
                GroupErrorCodes.ResourceNotFound,
                "The Group does not exist.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (current.Version != command.ExpectedVersion)
        {
            return await CompleteFailureAsync<GroupActivationResult>(
                lease,
                412,
                GroupErrorCodes.VersionConflict,
                "The Group version has changed.",
                unitOfWork,
                cancellationToken,
                ETag(current.Version)).ConfigureAwait(false);
        }

        if (current.Lifecycle == GroupLifecycle.Disabled
            && current.HasCurrentQuotaPeriod
            && command.SupplyEvidence is not null)
        {
            return null;
        }

        return await CompleteFailureAsync<GroupActivationResult>(
            lease,
            409,
            GroupErrorCodes.GroupActivationNotReady,
            "The Group does not satisfy its activation preconditions.",
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<GroupActivationResult?>> TryReplayAsync(
        GroupActivationOrchestrationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedActivation prepared;
        try
        {
            prepared = PrepareActivation(
                command.GroupId,
                command.ExpectedGroupVersion,
                command.IdempotencyKey,
                command.Reason,
                command.MetadataPatch);
        }
        catch (ArgumentException)
        {
            return Failure<GroupActivationResult?>(
                GroupErrorCodes.ValidationFailed,
                "The Group activation request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor.UserId, command.GroupId),
            command.IdempotencyKey,
            command.Actor.UserId,
            prepared.RequestHash,
            command.RequestId ?? EntityId.New(),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        return PreflightResult(acquire);
    }

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        string scope,
        string key,
        EntityId actorUserId,
        byte[] requestHash,
        EntityId owner,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                scope,
                key,
                EntityId.New(),
                $"user:{actorUserId.Value:D}",
                requestHash,
                owner,
                IdempotencyLease,
                IdempotencyRetention),
            unitOfWorkContext,
            cancellationToken);

    private static Result<GroupCommandOutcome>? ReplayOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        int expectedStatus) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure<GroupCommandOutcome>(
                GroupErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure<GroupCommandOutcome>(
                GroupErrorCodes.CoordinationUnavailable,
                "The matching idempotent command is still in progress.",
                retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => ReplayCommand(
                acquire.Response!,
                expectedStatus),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<GroupActivationResult>? ReplayActivationOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure<GroupActivationResult>(
                GroupErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure<GroupActivationResult>(
                GroupErrorCodes.CoordinationUnavailable,
                "The matching idempotent command is still in progress.",
                retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => ReplayActivation(acquire.Response!),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<GroupActivationResult?> PreflightResult(
        CommandIdempotencyAcquireResult acquire)
    {
        if (acquire.Disposition == CommandIdempotencyDisposition.Acquired)
        {
            return Result.Success<GroupActivationResult?>(null);
        }

        Result<GroupActivationResult> result = ReplayActivationOrAcquireFailure(acquire)
            ?? throw new InvalidOperationException(
                "The Group activation preflight returned an invalid disposition.");
        return result.IsSuccess
            ? Result.Success<GroupActivationResult?>(result.Value)
            : Failure<GroupActivationResult?>(
                result.Error.Code,
                result.Error.Description,
                result.Error.RetryAfterSeconds,
                result.Error.ETag,
                result.Error.Presentation);
    }

    private static Result<GroupCommandOutcome> ReplayCommand(
        CommandIdempotencyResponse response,
        int expectedStatus)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailure<GroupCommandOutcome>(response);
        }

        (GroupView view, string etag, string? location) = ParseSuccessReplay(response);
        if (response.Status != expectedStatus)
        {
            throw new InvalidOperationException(
                "The Group command replay status is invalid for this operation.");
        }

        return Result.Success(new GroupCommandOutcome(
            response.Status,
            true,
            view,
            etag,
            location));
    }

    private static Result<GroupActivationResult> ReplayActivation(
        CommandIdempotencyResponse response)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailure<GroupActivationResult>(response);
        }

        (GroupView view, _, _) = ParseSuccessReplay(response);
        if (response.Status != 200 || view.Status != GroupLifecycle.Active)
        {
            throw new InvalidOperationException(
                "The Group activation replay result is invalid.");
        }

        return Result.Success(new GroupActivationResult(
            view.Id,
            view.Status,
            view.Version,
            new GroupResourceSnapshot(
                view.Id,
                view.Name,
                view.Description,
                view.Status,
                view.Version,
                view.CreatedAt,
                view.UpdatedAt)));
    }

    private static Result<T> ReplayFailure<T>(CommandIdempotencyResponse response)
    {
        ReplayFailureBody failure = response.Body?.Deserialize<ReplayFailureBody>()
            ?? throw new InvalidOperationException("The Group failure replay body is invalid.");
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
            throw new InvalidOperationException("The Group failure replay is invalid.");
        }

        return Failure<T>(
            failure.Presentation.Code,
            failure.Description,
            etag: etag,
            presentation: failure.Presentation);
    }

    private static (GroupView View, string ETag, string? Location) ParseSuccessReplay(
        CommandIdempotencyResponse response)
    {
        GroupViewReplay replay = response.Body?.Deserialize<GroupViewReplay>()
            ?? throw new InvalidOperationException("The Group success replay body is invalid.");
        GroupView view = replay.ToView();
        string? etag = Header(response.Headers, "ETag");
        string? location = Header(response.Headers, "Location");
        int headerCount = response.Headers.ValueKind == JsonValueKind.Object
            ? response.Headers.EnumerateObject().Count()
            : -1;
        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status is not (200 or 201)
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "group", StringComparison.Ordinal)
            || response.ResourceId != view.Id
            || !string.Equals(etag, ETag(view.Version), StringComparison.Ordinal)
            || response.Status == 201
                && (!string.Equals(
                        location,
                        $"/api/v1/admin/groups/{view.Id.Value:D}",
                        StringComparison.Ordinal)
                    || headerCount != 2)
            || response.Status == 200 && (location is not null || headerCount != 1))
        {
            throw new InvalidOperationException("The Group success replay is invalid.");
        }

        return (view, etag!, location);
    }

    private async ValueTask<Result<T>> CompleteFailureAsync<T>(
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
            throw new InvalidOperationException("The Group idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(code, description, etag: etag, presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync(
        CommandIdempotencyLease lease,
        int status,
        GroupView view,
        string etag,
        string? location,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement headers = location is null
            ? JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ETag"] = etag,
                })
            : JsonSerializer.SerializeToElement(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ETag"] = etag,
                    ["Location"] = location,
                });
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                JsonSerializer.SerializeToElement(GroupViewReplay.From(view)),
                ResponseBodyEnvelope: null,
                headers,
                "group",
                view.Id),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The Group idempotency lease was lost.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        EntityId actorUserId,
        string action,
        EntityId targetId,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        JsonElement? before,
        JsonElement? after,
        string idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                AuditActorType.Admin,
                actorUserId,
                action,
                "group",
                targetId,
                requestId,
                reason,
                ipAddress,
                userAgent,
                before,
                after,
                JsonSerializer.SerializeToElement(new
                {
                    idempotency_key_hash = HmacText(
                        "poolai|audit-idempotency-key|groupquota|v1\0",
                        idempotencyKey),
                })),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask AppendEventAsync(
        string eventType,
        GroupResource group,
        EntityId requestId,
        JsonElement payload,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        EntityId eventId = EntityId.New();
        await _outboxAppender.AppendAsync(
            new IntegrationEvent(
                eventId,
                $"group:{eventType}:{eventId.Value:D}",
                EventTopic,
                EventSchemaVersion,
                "group",
                group.Id,
                group.Version,
                eventType,
                SourceEventSequence: null,
                requestId,
                CausationId: null,
                payload,
                _timeProvider.GetUtcNow()),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
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
            (GroupErrorCodes.GroupActivationNotReady, 409) =>
                ("Group activation not ready", "The Group does not satisfy its activation preconditions.", false),
            (GroupErrorCodes.VersionConflict, 412) =>
                ("Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
            _ => throw new InvalidOperationException(
                "The Group idempotent failure code and status are unsupported."),
        };
        return new ResultErrorPresentation(code, status, title, detail, retryable);
    }

    private static bool IsValidEvidence(SupplyReadinessEvidence? evidence)
    {
        if (evidence is null
            || evidence.ObservedAt == default
            || string.IsNullOrEmpty(evidence.OpaqueToken)
            || evidence.OpaqueToken.Length is < 4 or > 512)
        {
            return false;
        }

        int separator = evidence.OpaqueToken.IndexOf('.', StringComparison.Ordinal);
        if (separator < 1 || separator == evidence.OpaqueToken.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> prefix = evidence.OpaqueToken.AsSpan(0, separator);
        ReadOnlySpan<char> token = evidence.OpaqueToken.AsSpan(separator + 1);
        return prefix[0] is >= 'a' and <= 'z'
            && prefix[1..].ToArray().All(static character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9')
            && token.ToArray().All(static character =>
                character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '_' or '-');
    }

    private PreparedActivation PrepareActivation(
        EntityId groupId,
        long expectedVersion,
        string idempotencyKey,
        string reason,
        GroupMetadataPatch? metadata)
    {
        GroupInput.IdempotencyKey(idempotencyKey);
        string normalizedReason = GroupInput.Reason(reason);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);

        bool hasName = metadata?.HasName is true;
        bool hasDescription = metadata?.HasDescription is true;
        string? normalizedName = hasName
            ? GroupInput.Name(metadata!.Name ?? string.Empty)
            : null;
        string? normalizedDescription = hasDescription
            ? GroupInput.Description(metadata!.Description)
            : null;
        return new PreparedActivation(
            hasName,
            normalizedName,
            hasDescription,
            normalizedDescription,
            normalizedReason,
            HashRequest(new
            {
                group_id = groupId.Value,
                expected_version = expectedVersion,
                has_name = hasName,
                name = normalizedName,
                has_description = hasDescription,
                description = normalizedDescription,
                reason = normalizedReason,
            }));
    }

    private static bool IsAdmin(GroupActor actor) =>
        actor.TokenVersion > 0 && actor.Role == GroupControlRole.Admin;

    private static bool CanRead(GroupActor actor) =>
        actor.TokenVersion > 0
        && actor.Role is GroupControlRole.Admin
            or GroupControlRole.Operator
            or GroupControlRole.Auditor;

    private static GroupView ToView(GroupResource group) => new(
        group.Id,
        group.Name,
        group.Description,
        group.Lifecycle,
        group.Version,
        group.CreatedAt,
        group.UpdatedAt);

    private static JsonElement AuditState(GroupResource group) =>
        JsonSerializer.SerializeToElement(new
        {
            group_id = group.Id.Value,
            name = group.Name,
            description = group.Description,
            status = LifecycleCode(group.Lifecycle),
            version = group.Version,
        });

    private static string[] ChangedFields(GroupResource before, GroupResource after)
    {
        List<string> fields = new(3);
        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
        {
            fields.Add("name");
        }

        if (!string.Equals(before.Description, after.Description, StringComparison.Ordinal))
        {
            fields.Add("description");
        }

        if (before.Lifecycle != after.Lifecycle)
        {
            fields.Add("status");
        }

        return fields.ToArray();
    }

    private static string LifecycleCode(GroupLifecycle lifecycle) => lifecycle switch
    {
        GroupLifecycle.Active => "active",
        GroupLifecycle.Disabled => "disabled",
        GroupLifecycle.Archived => "archived",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };

    private static string CreateScope(GroupActor actor) =>
        $"groupquota:{actor.UserId.Value:D}:post:/api/v1/admin/groups";

    private static string UpdateScope(EntityId actorUserId, EntityId groupId) =>
        $"groupquota:{actorUserId.Value:D}:patch:/api/v1/admin/groups/{groupId.Value:D}";

    private static string ETag(long version) => $"\"v{version}\"";

    private static bool IsCanonicalETag(string etag) =>
        etag.Length >= 4
        && etag[0] == '"'
        && etag[1] == 'v'
        && etag[2] is >= '1' and <= '9'
        && etag[^1] == '"'
        && long.TryParse(
            etag.AsSpan(2, etag.Length - 3),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private byte[] HashRequest<T>(T request)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|groupquota|v1\0");
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

    private string QuotaIdempotencyKey(string scope, string key) =>
        $"group-create:{HmacText("poolai|quota-idempotency-key|groupquota|v1\0", scope + "\0" + key)}";

    private static string EncodeCursor(GroupResource group)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (group.CreatedAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(1, 8), unixMicroseconds);
        Convert.FromHexString(group.Id.Value.ToString("N"), bytes[9..], out _, out _);
        return ToBase64Url(bytes);
    }

    private static bool TryDecodeCursor(string? encoded, out GroupCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        {
            return true;
        }

        try
        {
            if (encoded.Length != 34
                || encoded.Contains('=', StringComparison.Ordinal)
                || encoded.Any(static character =>
                    !(character is >= 'A' and <= 'Z'
                        or >= 'a' and <= 'z'
                        or >= '0' and <= '9'
                        or '-' or '_')))
            {
                return false;
            }

            string base64 = encoded.Replace('-', '+').Replace('_', '/') + "==";
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 25
                || bytes[0] != 0x01
                || !string.Equals(ToBase64Url(bytes), encoded, StringComparison.Ordinal))
            {
                return false;
            }

            long unixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
            long ticks = checked(DateTime.UnixEpoch.Ticks + checked(unixMicroseconds * 10));
            bool validId = Guid.TryParseExact(
                Convert.ToHexString(bytes.AsSpan(9, 16)),
                "N",
                out Guid id);
            if (!validId
                || id == Guid.Empty
                || ticks < DateTimeOffset.MinValue.UtcDateTime.Ticks
                || ticks > DateTimeOffset.MaxValue.UtcDateTime.Ticks)
            {
                return false;
            }

            cursor = new GroupCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new EntityId(id));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
            && headers.TryGetProperty(name, out JsonElement value)
                ? value.GetString()
                : null;

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

    private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record ReplayFailureBody(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record PreparedActivation(
        bool HasName,
        string? Name,
        bool HasDescription,
        string? Description,
        string Reason,
        byte[] RequestHash);

    private sealed record GroupViewReplay(
        Guid Id,
        string Name,
        string? Description,
        GroupLifecycle Status,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static GroupViewReplay From(GroupView value) => new(
            value.Id.Value,
            value.Name,
            value.Description,
            value.Status,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal GroupView ToView()
        {
            if (Id == Guid.Empty
                || string.IsNullOrWhiteSpace(Name)
                || Version <= 0
                || CreatedAt == default
                || UpdatedAt == default)
            {
                throw new InvalidOperationException("The Group replay body is invalid.");
            }

            _ = LifecycleCode(Status);
            return new GroupView(
                new EntityId(Id),
                Name,
                Description,
                Status,
                Version,
                CreatedAt,
                UpdatedAt);
        }
    }
}
#pragma warning restore MA0051
