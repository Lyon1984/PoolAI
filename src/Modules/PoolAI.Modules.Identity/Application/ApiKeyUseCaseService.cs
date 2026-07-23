#pragma warning disable MA0051 // The complete idempotency and single-UoW sequence is intentionally visible.
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed partial class ApiKeyUseCaseService :
    IApiKeyControlPlaneReader,
    IApiKeyCreateIdempotencyPreflight,
    IApiKeyIssuer,
    IApiKeyMutationIdempotencyPreflight,
    IApiKeyMutationOwner
{
    private const string ResourceType = "api_key";
    private const string CacheControl = "no-store";
    private const string ReplayIntegrityEventName =
        "identity.api_key.idempotency_replay_integrity_failed";
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IApiKeyRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IApiKeyCredentialService _credentialService;
    private readonly IApiKeyCreateResponseEnvelope _responseEnvelope;
    private readonly IOperationalEventWriter _operationalEventWriter;
    private readonly IdentityPolicy _policy;

    internal ApiKeyUseCaseService(
        IApiKeyRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IApiKeyCredentialService credentialService,
        IApiKeyCreateResponseEnvelope responseEnvelope,
        IOperationalEventWriter operationalEventWriter,
        IdentityPolicy policy)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory
            ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore
            ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _credentialService = credentialService
            ?? throw new ArgumentNullException(nameof(credentialService));
        _responseEnvelope = responseEnvelope
            ?? throw new ArgumentNullException(nameof(responseEnvelope));
        _operationalEventWriter = operationalEventWriter
            ?? throw new ArgumentNullException(nameof(operationalEventWriter));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async ValueTask<Result<ApiKeyPage>> ListAsync(
        ListApiKeysQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanAccess(query.Actor, query.AccessMode, query.UserId))
        {
            return Failure<ApiKeyPage>(
                IdentityErrorCodes.RoleRequired,
                "The actor cannot read API Keys for the requested user.");
        }

        if (query.Limit is < 1 or > 100
            || !TryDecodeCursor(query.Cursor, out ApiKeyCursor? cursor))
        {
            return Failure<ApiKeyPage>(
                IdentityErrorCodes.InvalidRequest,
                "The API Key pagination request is invalid.");
        }

        ApiKeySlice slice = await _repository
            .ListAsync(query.UserId, cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        if (!IsValidListSlice(slice, query.UserId, cursor, query.Limit))
        {
            throw new InvalidOperationException(
                "The API Key repository returned an invalid list.");
        }

        string? nextCursor = slice.HasMore && slice.ApiKeys.Count > 0
            ? EncodeCursor(slice.ApiKeys[^1])
            : null;
        return Result.Success(new ApiKeyPage(
            slice.ApiKeys.Select(static value => value.ToSnapshot()).ToArray(),
            nextCursor,
            slice.HasMore));
    }

    public async ValueTask<Result<ApiKeyControlPlaneSnapshot>> GetAsync(
        GetApiKeyQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanAccess(query.Actor, query.AccessMode, query.UserId))
        {
            return Failure<ApiKeyControlPlaneSnapshot>(
                IdentityErrorCodes.RoleRequired,
                "The actor cannot read API Keys for the requested user.");
        }

        ApiKeyResource? apiKey = await _repository
            .GetAsync(query.UserId, query.ApiKeyId, cancellationToken)
            .ConfigureAwait(false);
        if (apiKey is not null
            && (!ApiKeyResourceValidator.IsValid(apiKey)
                || apiKey.UserId != query.UserId
                || apiKey.Id != query.ApiKeyId))
        {
            throw new InvalidOperationException(
                "The API Key repository returned a mismatched resource.");
        }

        return apiKey is null
            ? Failure<ApiKeyControlPlaneSnapshot>(
                IdentityErrorCodes.ResourceNotFound,
                "The API Key does not exist.")
            : Result.Success(apiKey.ToSnapshot());
    }

    public async ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedCreate prepared;
        try
        {
            prepared = Prepare(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyCreatedOutcome?>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key create request is invalid.");
        }

        try
        {
            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable unitOfWorkLease =
                unitOfWork.ConfigureAwait(false);
            CommandIdempotencyAcquireResult acquire = await AcquireAsync(
                command,
                prepared,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            return PreflightResult(command, prepared, acquire);
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteReplayIntegrityFailureAsync(
                command,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyCreatedOutcome?>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
        CreateApiKeyCommand command,
        ApiKeyAccessDecision accessDecision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(accessDecision);
        PreparedCreate prepared;
        try
        {
            prepared = Prepare(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyCreatedOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key create request is invalid.");
        }

        try
        {
            Result<Unit> decisionValidation = ValidateAccessDecision(
                command,
                accessDecision);
            if (decisionValidation.IsFailure)
            {
                return ForwardFailure<ApiKeyCreatedOutcome>(
                    decisionValidation.Error);
            }

            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable unitOfWorkLease =
                unitOfWork.ConfigureAwait(false);
            CommandIdempotencyAcquireResult acquire = await AcquireAsync(
                command,
                prepared,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyCreatedOutcome>? early = ReplayOrAcquireFailure(
                command,
                prepared,
                acquire);
            if (early is not null)
            {
                return early;
            }

            CommandIdempotencyLease lease = acquire.Lease!;
            if (accessDecision.Kind != ApiKeyAccessDecisionKind.Authorized)
            {
                return await CompleteAccessFailureAsync(
                    accessDecision.Kind,
                    lease,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            ApiKeyCredential credential = _credentialService.Create();
            try
            {
                EntityId apiKeyId = EntityId.New();
                ApiKeyCreateResult created = await _repository.CreateAsync(
                    new ApiKeyCreateWrite(
                        apiKeyId,
                        command.UserId,
                        command.GroupId,
                        prepared.Name,
                        credential.DisplayPrefix,
                        credential.Hash,
                        credential.PepperVersion,
                        prepared.ExpiresAt,
                        prepared.AllowedCidrs),
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                if (created.Disposition != ApiKeyCreateDisposition.Created)
                {
                    return await CompleteCreateFailureAsync(
                        created.Disposition,
                        lease,
                        unitOfWork,
                        cancellationToken).ConfigureAwait(false);
                }

                ApiKeyResource createdResource = created.ApiKey
                    ?? throw new InvalidOperationException(
                        "The API Key repository omitted the created resource.");
                ValidateCreatedResource(
                    createdResource,
                    apiKeyId,
                    command,
                    prepared,
                    credential);
                ApiKeyControlPlaneSnapshot snapshot = createdResource.ToSnapshot();
                await AppendAuditAsync(
                    command,
                    prepared.Reason,
                    snapshot,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                string etag = ETag(snapshot.Version);
                string location = Location(command, snapshot.ApiKeyId);
                ApiKeyCreateResponseSecret secretResponse = new(
                    snapshot,
                    credential.Secret,
                    etag,
                    location);
                JsonElement responseEnvelope = _responseEnvelope.Encrypt(
                    secretResponse,
                    snapshot.ApiKeyId,
                    Binding(command, prepared));
                bool completed = await _idempotencyStore.CompleteAsync(
                    new CommandIdempotencyCompletion(
                        lease,
                        CommandIdempotencyTerminalStatus.Completed,
                        ResponseStatus: 201,
                        ResponseBody: null,
                        ResponseBodyEnvelope: responseEnvelope,
                        ResponseHeaders: SuccessHeaders(etag, location),
                        ResourceType,
                        snapshot.ApiKeyId),
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                if (!completed)
                {
                    throw new InvalidOperationException(
                        "The API Key idempotency lease was lost before completion.");
                }

                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success(new ApiKeyCreatedOutcome(
                    201,
                    IsReplay: false,
                    snapshot,
                    credential.Secret,
                    etag,
                    location));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credential.Hash);
            }
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteReplayIntegrityFailureAsync(
                command,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyCreatedOutcome>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    private Result<ApiKeyCreatedOutcome?> PreflightResult(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        CommandIdempotencyAcquireResult acquire) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired =>
                Result.Success<ApiKeyCreatedOutcome?>(null),
            CommandIdempotencyDisposition.Conflict =>
                Failure<ApiKeyCreatedOutcome?>(
                    IdentityErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<ApiKeyCreatedOutcome?>(
                    IdentityErrorCodes.CoordinationUnavailable,
                    "The matching idempotent API Key command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => ReplayNullable(
                command,
                prepared,
                RequireReplayResponse(acquire)),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private Result<ApiKeyCreatedOutcome>? ReplayOrAcquireFailure(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        CommandIdempotencyAcquireResult acquire) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict =>
                Failure<ApiKeyCreatedOutcome>(
                    IdentityErrorCodes.IdempotencyConflict,
                    "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy =>
                Failure<ApiKeyCreatedOutcome>(
                    IdentityErrorCodes.CoordinationUnavailable,
                    "The matching idempotent API Key command is still in progress.",
                    retryAfterSeconds: 1),
            CommandIdempotencyDisposition.Replay => Replay(
                command,
                prepared,
                RequireReplayResponse(acquire)),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static CommandIdempotencyResponse RequireReplayResponse(
        CommandIdempotencyAcquireResult acquire) =>
        acquire.Response
        ?? throw new ApiKeyReplayIntegrityException(
            responseResourceId: null,
            new InvalidOperationException(
                "The API Key idempotency replay response is missing."));

    private Result<ApiKeyCreatedOutcome?> ReplayNullable(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        CommandIdempotencyResponse response)
    {
        Result<ApiKeyCreatedOutcome> replay = Replay(command, prepared, response);
        return replay.IsFailure
            ? ForwardFailure<ApiKeyCreatedOutcome?>(replay.Error)
            : Result.Success<ApiKeyCreatedOutcome?>(replay.Value);
    }

    private Result<ApiKeyCreatedOutcome> Replay(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        CommandIdempotencyResponse response)
    {
        try
        {
            return ReplayCore(command, prepared, response);
        }
        catch (Exception exception) when (ShouldWrapReplayFailure(exception))
        {
            throw new ApiKeyReplayIntegrityException(
                response.ResourceId,
                exception);
        }
    }

    private Result<ApiKeyCreatedOutcome> ReplayCore(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        CommandIdempotencyResponse response)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailureResult(response);
        }

        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 201
            || response.Body is not null
            || response.BodyEnvelope is null
            || response.ResourceId is null
            || !string.Equals(response.ResourceType, ResourceType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The API Key idempotency replay is invalid.");
        }

        string? etag = Header(response.Headers, "ETag");
        string? location = Header(response.Headers, "Location");
        string? cacheControl = Header(response.Headers, "Cache-Control");
        if (!HasOnlySuccessHeaders(response.Headers)
            || !string.Equals(cacheControl, CacheControl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The API Key idempotency replay headers are invalid.");
        }

        ApiKeyCreateResponseSecret secret = _responseEnvelope.Decrypt(
            response.BodyEnvelope.Value,
            response.ResourceId.Value,
            Binding(command, prepared));
        ApiKeyControlPlaneSnapshot apiKey = secret.ApiKey;
        bool hasValidSecret = _credentialService.TryGetDisplayPrefix(
            secret.Secret,
            out string? displayPrefix);
        if (!ApiKeyResourceValidator.IsValid(ToResource(apiKey))
            || apiKey.ApiKeyId != response.ResourceId.Value
            || apiKey.UserId != command.UserId
            || apiKey.GroupId != command.GroupId
            || !string.Equals(apiKey.Name, prepared.Name, StringComparison.Ordinal)
            || apiKey.ExpiresAt != prepared.ExpiresAt
            || !apiKey.AllowedCidrs.SequenceEqual(
                prepared.AllowedCidrs,
                StringComparer.Ordinal)
            || !hasValidSecret
            || !string.Equals(
                apiKey.Prefix,
                displayPrefix,
                StringComparison.Ordinal)
            || apiKey.Status != ApiKeyPersistentStatus.Active
            || apiKey.EffectiveStatus is not (
                ApiKeyEffectiveStatus.Active or ApiKeyEffectiveStatus.Expired)
            || apiKey.Version != 1
            || apiKey.LastUsedAt is not null
            || apiKey.CreatedAt == default
            || apiKey.UpdatedAt == default
            || apiKey.ObservedAt == default
            || apiKey.CreatedAt != apiKey.UpdatedAt
            || !string.Equals(secret.ETag, etag, StringComparison.Ordinal)
            || !string.Equals(secret.ETag, ETag(apiKey.Version), StringComparison.Ordinal)
            || !string.Equals(secret.Location, location, StringComparison.Ordinal)
            || !string.Equals(
                secret.Location,
                Location(command, apiKey.ApiKeyId),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The API Key idempotency response envelope is invalid.");
        }

        return Result.Success(new ApiKeyCreatedOutcome(
            201,
            IsReplay: true,
            apiKey,
            secret.Secret,
            secret.ETag,
            secret.Location));
    }

    private static Result<ApiKeyCreatedOutcome> ReplayFailureResult(
        CommandIdempotencyResponse response)
    {
        ReplayFailure failure = response.Body?.Deserialize<ReplayFailure>()
            ?? throw new InvalidOperationException(
                "The API Key failure replay body is invalid.");
        if (string.IsNullOrWhiteSpace(failure.Description)
            || failure.Presentation is null)
        {
            throw new InvalidOperationException(
                "The API Key failure replay body is invalid.");
        }

        ResultErrorPresentation presentation = failure.Presentation;
        ResultErrorPresentation expected = FailurePresentation(
            presentation.Status,
            presentation.Code);
        if (response.Status != presentation.Status
            || !PresentationsEqual(presentation, expected)
            || response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || !HasNoHeaders(response.Headers))
        {
            throw new InvalidOperationException(
                "The API Key failure replay is invalid.");
        }

        return Failure<ApiKeyCreatedOutcome>(
            presentation.Code,
            failure.Description,
            presentation: presentation);
    }

    private async ValueTask WriteReplayIntegrityFailureAsync(
        CreateApiKeyCommand command,
        ApiKeyReplayIntegrityException exception,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            request_id = command.RequestId.Value,
            actor_user_id = command.Actor.UserId.Value,
            target_user_id = command.UserId.Value,
            group_id = command.GroupId.Value,
            access_mode = AccessModeName(command.AccessMode),
            response_resource_id = exception.ResponseResourceId?.Value,
        });
        await _operationalEventWriter.WriteAsync(
            ReplayIntegrityEventName,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<ApiKeyCreatedOutcome>> CompleteAccessFailureAsync(
        ApiKeyAccessDecisionKind kind,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => kind switch
        {
            ApiKeyAccessDecisionKind.SubscriptionRequired =>
                await CompleteFailureAsync(
                    lease,
                    403,
                    "subscription_required",
                    "No canonical Subscription exists for the requested Group.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            ApiKeyAccessDecisionKind.SubscriptionInactive =>
                await CompleteFailureAsync(
                    lease,
                    403,
                    "subscription_inactive",
                    "The canonical Subscription is not currently active.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private async ValueTask<Result<ApiKeyCreatedOutcome>> CompleteCreateFailureAsync(
        ApiKeyCreateDisposition disposition,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => disposition switch
        {
            ApiKeyCreateDisposition.Conflict =>
                await CompleteFailureAsync(
                    lease,
                    409,
                    IdentityErrorCodes.ResourceConflict,
                    "The API Key could not be created for the requested resources.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            ApiKeyCreateDisposition.ValidationFailed =>
                await CompleteFailureAsync(
                    lease,
                    422,
                    IdentityErrorCodes.ValidationFailed,
                    "The API Key create request is no longer valid.",
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };

    private async ValueTask<Result<ApiKeyCreatedOutcome>> CompleteFailureAsync(
        CommandIdempotencyLease lease,
        int status,
        string code,
        string description,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        ResultErrorPresentation presentation = FailurePresentation(status, code);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                status,
                JsonSerializer.SerializeToElement(
                    new ReplayFailure(description, presentation)),
                ResponseBodyEnvelope: null,
                EmptyObject,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The API Key idempotency lease was lost before failure completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<ApiKeyCreatedOutcome>(
            code,
            description,
            presentation: presentation);
    }

    private async ValueTask AppendAuditAsync(
        CreateApiKeyCommand command,
        string? reason,
        ApiKeyControlPlaneSnapshot apiKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
        new AuditEntry(
            EntityId.New(),
            ActorType(command.Actor.Role),
            command.Actor.UserId,
            "identity.api_key.created",
            ResourceType,
            apiKey.ApiKeyId,
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            BeforeState: null,
            AfterState: AuditState(apiKey),
            Metadata: JsonSerializer.SerializeToElement(new
            {
                idempotency_key_hash = HmacText(
                    "poolai|audit-idempotency-key|identity|v1\0",
                    command.IdempotencyKey),
            })),
        unitOfWorkContext,
        cancellationToken).ConfigureAwait(false);

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
        new CommandIdempotencyRequest(
            prepared.Scope,
            command.IdempotencyKey,
            EntityId.New(),
            ActorFingerprint(command.Actor),
            prepared.RequestHash,
            command.RequestId,
            IdempotencyLease,
            IdempotencyRetention),
        unitOfWorkContext,
        cancellationToken);

    private PreparedCreate Prepare(CreateApiKeyCommand command)
    {
        if (!CanAccess(command.Actor, command.AccessMode, command.UserId)
            || command.RequestId.Value == Guid.Empty
            || command.GroupId.Value == Guid.Empty)
        {
            throw new ArgumentException("The API Key actor or target is invalid.", nameof(command));
        }

        IdentityInput.IdempotencyKey(command.IdempotencyKey);
        string name = ApiKeyInput.Name(command.Name);
        IReadOnlyList<string> allowedCidrs =
            ApiKeyInput.AllowedCidrs(command.AllowedCidrs);
        string? reason = command.AccessMode switch
        {
            ApiKeyAccessMode.Self when command.Reason is null => null,
            ApiKeyAccessMode.AdminProxy when command.Reason is not null =>
                ApiKeyInput.AdminReason(command.Reason),
            _ => throw new ArgumentException(
                "The API Key create reason is invalid.",
                nameof(command)),
        };
        DateTimeOffset? expiresAt = command.ExpiresAt is DateTimeOffset expiration
            ? ApiKeyInput.PostgresTimestamp(expiration)
            : null;
        string scope = CreateScope(command);
        byte[] requestHash = HashRequest(new
        {
            user_id = command.UserId.Value,
            group_id = command.GroupId.Value,
            name,
            expires_at = expiresAt,
            allowed_cidrs = allowedCidrs,
            reason,
        });
        return new PreparedCreate(
            name,
            expiresAt,
            allowedCidrs,
            reason,
            scope,
            requestHash);
    }

    private static Result<Unit> ValidateAccessDecision(
        CreateApiKeyCommand command,
        ApiKeyAccessDecision decision)
    {
        if (decision.UserId != command.UserId
            || decision.GroupId != command.GroupId)
        {
            return Failure<Unit>(
                IdentityErrorCodes.DependencyUnavailable,
                "The Subscription access decision does not match the API Key target.",
                retryAfterSeconds: 1);
        }

        bool hasEvidence = decision.SubscriptionId is EntityId subscriptionId
            && subscriptionId.Value != Guid.Empty
            && decision.ObservedAt is DateTimeOffset observedAt
            && observedAt != default;
        if (decision.Kind == ApiKeyAccessDecisionKind.Authorized != hasEvidence
            || decision.Kind != ApiKeyAccessDecisionKind.Authorized
            && (decision.SubscriptionId is not null || decision.ObservedAt is not null))
        {
            return Failure<Unit>(
                IdentityErrorCodes.DependencyUnavailable,
                "The Subscription access decision is internally inconsistent.",
                retryAfterSeconds: 1);
        }

        return Result.Success(Unit.Value);
    }

    private static void ValidateCreatedResource(
        ApiKeyResource value,
        EntityId apiKeyId,
        CreateApiKeyCommand command,
        PreparedCreate prepared,
        ApiKeyCredential credential)
    {
        if (!ApiKeyResourceValidator.IsValid(value)
            || value.Id != apiKeyId
            || value.UserId != command.UserId
            || value.GroupId != command.GroupId
            || !string.Equals(value.Name, prepared.Name, StringComparison.Ordinal)
            || !string.Equals(value.Prefix, credential.DisplayPrefix, StringComparison.Ordinal)
            || value.Status != ApiKeyPersistentStatus.Active
            || value.EffectiveStatus is not (
                ApiKeyEffectiveStatus.Active or ApiKeyEffectiveStatus.Expired)
            || value.ExpiresAt != prepared.ExpiresAt
            || !value.AllowedCidrs.SequenceEqual(
                prepared.AllowedCidrs,
                StringComparer.Ordinal)
            || value.LastUsedAt is not null
            || value.Version != 1
            || value.CreatedAt == default
            || value.UpdatedAt == default
            || value.ObservedAt == default
            || value.CreatedAt != value.UpdatedAt
            || value.ObservedAt < value.CreatedAt)
        {
            throw new InvalidOperationException(
                "The API Key repository returned an inconsistent created resource.");
        }
    }

    private static bool IsValidListSlice(
        ApiKeySlice slice,
        EntityId userId,
        ApiKeyCursor? cursor,
        int limit)
    {
        if (slice.ApiKeys.Count > limit
            || slice.HasMore && slice.ApiKeys.Count != limit)
        {
            return false;
        }

        HashSet<EntityId> ids = [];
        ApiKeyResource? previous = null;
        foreach (ApiKeyResource value in slice.ApiKeys)
        {
            if (!ApiKeyResourceValidator.IsValid(value)
                || value.UserId != userId
                || !ids.Add(value.Id)
                || cursor is not null && !IsAfterCursor(value, cursor)
                || previous is not null && !IsAfterCursor(value, Cursor(previous)))
            {
                return false;
            }

            previous = value;
        }

        return true;
    }

    private static bool IsAfterCursor(
        ApiKeyResource value,
        ApiKeyCursor cursor) =>
        value.CreatedAt < cursor.CreatedAt
        || value.CreatedAt == cursor.CreatedAt
        && string.CompareOrdinal(
            value.Id.Value.ToString("N"),
            cursor.Id.Value.ToString("N")) < 0;

    private static ApiKeyCursor Cursor(ApiKeyResource value) =>
        new(value.CreatedAt, value.Id);

    private static ApiKeyResource ToResource(ApiKeyControlPlaneSnapshot value) => new(
        value.ApiKeyId,
        value.UserId,
        value.GroupId,
        value.Name,
        value.Prefix,
        value.Status,
        value.EffectiveStatus,
        value.ExpiresAt,
        value.AllowedCidrs,
        value.LastUsedAt,
        value.Version,
        value.CreatedAt,
        value.UpdatedAt,
        value.ObservedAt);

    private static bool CanAccess(
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId)
    {
        if (actor.UserId.Value == Guid.Empty
            || userId.Value == Guid.Empty
            || actor.TokenVersion <= 0
            || !Enum.IsDefined(actor.Role))
        {
            return false;
        }

        return accessMode switch
        {
            ApiKeyAccessMode.Self => actor.UserId == userId,
            ApiKeyAccessMode.AdminProxy => actor.Role == SystemRole.Admin,
            _ => false,
        };
    }

    private static string AccessModeName(ApiKeyAccessMode accessMode) => accessMode switch
    {
        ApiKeyAccessMode.Self => "self",
        ApiKeyAccessMode.AdminProxy => "admin_proxy",
        _ => "unknown",
    };

    private static bool ShouldWrapReplayFailure(Exception exception) =>
        exception is not OperationCanceledException
        and not OutOfMemoryException
        and not StackOverflowException
        and not AccessViolationException;

    private static ResultErrorPresentation FailurePresentation(
        int status,
        string code)
    {
        (string title, string detail) = (code, status) switch
        {
            ("subscription_required", 403) =>
                ("Subscription required", "No Subscription grants access to the requested Group."),
            ("subscription_inactive", 403) =>
                ("Subscription inactive", "The Subscription does not currently grant access."),
            (IdentityErrorCodes.ValidationFailed, 422) =>
                ("Validation failed", "The request failed validation."),
            (IdentityErrorCodes.ResourceConflict, 409) =>
                ("Resource conflict", "The request conflicts with the current resource state."),
            _ => throw new InvalidOperationException(
                "The API Key failure status and code are not supported."),
        };
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors =
            string.Equals(code, IdentityErrorCodes.ValidationFailed, StringComparison.Ordinal)
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["/expires_at"] =
                    [
                        "The expiration must still be later than PostgreSQL time.",
                    ],
                }
                : null;
        return new ResultErrorPresentation(
            code,
            status,
            title,
            detail,
            Retryable: false,
            Errors: errors);
    }

    private static bool PresentationsEqual(
        ResultErrorPresentation left,
        ResultErrorPresentation right)
    {
        if (!string.Equals(left.Code, right.Code, StringComparison.Ordinal)
            || left.Status != right.Status
            || !string.Equals(left.Title, right.Title, StringComparison.Ordinal)
            || !string.Equals(left.Detail, right.Detail, StringComparison.Ordinal)
            || left.Retryable != right.Retryable
            || left.RetryAfterSeconds != right.RetryAfterSeconds)
        {
            return false;
        }

        if (left.Errors is null || right.Errors is null)
        {
            return left.Errors is null && right.Errors is null;
        }

        return left.Errors.Count == right.Errors.Count
            && left.Errors.All(pair =>
                right.Errors.TryGetValue(pair.Key, out IReadOnlyList<string>? values)
                && pair.Value.SequenceEqual(values, StringComparer.Ordinal));
    }

    private static JsonElement AuditState(ApiKeyControlPlaneSnapshot value) =>
        JsonSerializer.SerializeToElement(new
        {
            api_key_id = value.ApiKeyId.Value,
            user_id = value.UserId.Value,
            group_id = value.GroupId.Value,
            name = value.Name,
            prefix = value.Prefix,
            status = PersistentStatus(value.Status),
            effective_status = EffectiveStatus(value.EffectiveStatus),
            expires_at = value.ExpiresAt,
            allowed_cidrs = value.AllowedCidrs,
            last_used_at = value.LastUsedAt,
            version = value.Version,
            created_at = value.CreatedAt,
            updated_at = value.UpdatedAt,
        });

    private static JsonElement SuccessHeaders(string etag, string location) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
                ["Location"] = location,
                ["Cache-Control"] = CacheControl,
            });

    private static bool HasOnlySuccessHeaders(JsonElement headers)
    {
        if (headers.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonProperty[] values = headers.EnumerateObject().ToArray();
        return values.Length == 3
            && values.All(property =>
                property.Value.ValueKind == JsonValueKind.String
                && property.Name is "ETag" or "Location" or "Cache-Control");
    }

    private static bool HasNoHeaders(JsonElement headers) =>
        headers.ValueKind == JsonValueKind.Object
        && !headers.EnumerateObject().Any();

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
        && headers.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IdempotencySecretBinding Binding(
        CreateApiKeyCommand command,
        PreparedCreate prepared) => new(
        command.Actor.UserId,
        prepared.Scope,
        command.IdempotencyKey,
        prepared.RequestHash);

    private static string CreateScope(CreateApiKeyCommand command) =>
        command.AccessMode switch
        {
            ApiKeyAccessMode.Self =>
                $"identity:{command.Actor.UserId.Value:D}:post:/api/v1/me/api-keys",
            ApiKeyAccessMode.AdminProxy =>
                $"identity:{command.Actor.UserId.Value:D}:post:/api/v1/admin/users/{command.UserId.Value:D}/api-keys",
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };

    private static string Location(
        CreateApiKeyCommand command,
        EntityId apiKeyId) => command.AccessMode switch
    {
        ApiKeyAccessMode.Self =>
            $"/api/v1/me/api-keys/{apiKeyId.Value:D}",
        ApiKeyAccessMode.AdminProxy =>
            $"/api/v1/admin/users/{command.UserId.Value:D}/api-keys/{apiKeyId.Value:D}",
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private byte[] HashRequest<T>(T request)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] domain = Encoding.UTF8.GetBytes(
            "poolai|idempotency-request-hash|identity|v1\0");
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

    private static string EncodeCursor(ApiKeyResource apiKey)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (apiKey.CreatedAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(1, 8), unixMicroseconds);
        Convert.FromHexString(apiKey.Id.Value.ToString("N"), bytes[9..], out _, out _);
        return ToBase64Url(bytes);
    }

    private static bool TryDecodeCursor(
        string? value,
        out ApiKeyCursor? cursor)
    {
        cursor = null;
        if (value is null)
        {
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(value);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (bytes.Length != 25 || bytes[0] != 0x01)
            {
                return false;
            }

            long unixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(
                bytes.AsSpan(1, 8));
            long ticks = checked(
                DateTime.UnixEpoch.Ticks + checked(unixMicroseconds * 10));
            DateTimeOffset createdAt = new(
                new DateTime(ticks, DateTimeKind.Utc));
            Guid id = new(bytes.AsSpan(9, 16), bigEndian: true);
            if (id == Guid.Empty)
            {
                return false;
            }

            cursor = new ApiKeyCursor(createdAt, new EntityId(id));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        if (value.Length == 0
            || value.Contains('=', StringComparison.Ordinal)
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new FormatException("The cursor is not canonical base64url.");
        }

        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("The cursor base64url length is invalid."),
        };
        byte[] decoded = Convert.FromBase64String(base64);
        if (!string.Equals(ToBase64Url(decoded), value, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("The cursor is not canonical base64url.");
        }

        return decoded;
    }

    private static string ActorFingerprint(ApiKeyActor actor) =>
        $"user:{actor.UserId.Value:D}";

    private static AuditActorType ActorType(SystemRole role) => role switch
    {
        SystemRole.Admin => AuditActorType.Admin,
        SystemRole.Operator => AuditActorType.Operator,
        SystemRole.Auditor => AuditActorType.Auditor,
        SystemRole.User => AuditActorType.User,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string ETag(long version) =>
        string.Create(CultureInfo.InvariantCulture, $"\"v{version}\"");

    private static string PersistentStatus(ApiKeyPersistentStatus value) => value switch
    {
        ApiKeyPersistentStatus.Active => "active",
        ApiKeyPersistentStatus.Disabled => "disabled",
        ApiKeyPersistentStatus.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string EffectiveStatus(ApiKeyEffectiveStatus value) => value switch
    {
        ApiKeyEffectiveStatus.Active => "active",
        ApiKeyEffectiveStatus.Disabled => "disabled",
        ApiKeyEffectiveStatus.Expired => "expired",
        ApiKeyEffectiveStatus.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        ResultErrorPresentation? presentation = null) => Result.Failure<T>(
        code,
        description,
        retryAfterSeconds,
        etag: null,
        presentation);

    private static Result<T> ForwardFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);

    private sealed record PreparedCreate(
        string Name,
        DateTimeOffset? ExpiresAt,
        IReadOnlyList<string> AllowedCidrs,
        string? Reason,
        string Scope,
        byte[] RequestHash)
    {
        internal void Clear() => CryptographicOperations.ZeroMemory(RequestHash);
    }

    private sealed record ReplayFailure(
        string? Description,
        ResultErrorPresentation? Presentation);

    private sealed class ApiKeyReplayIntegrityException(
        EntityId? responseResourceId,
        Exception innerException)
        : Exception(
            "The API Key idempotency replay failed integrity validation.",
            innerException)
    {
        internal EntityId? ResponseResourceId { get; } = responseResourceId;
    }
}
#pragma warning restore MA0051
