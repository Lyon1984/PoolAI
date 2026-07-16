#pragma warning disable MA0051 // Command handlers keep the complete transactional sequence visible.
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed class IdentityUseCaseService :
    IListUsersUseCase,
    IGetUserUseCase,
    ICreateUserUseCase,
    IUpdateUserUseCase,
    IRequestAdminPasswordResetUseCase,
    IRequestPasswordResetUseCase,
    ICompletePasswordResetUseCase
{
    private const string EventTopic = "poolai.identity.v1";
    private const int EventSchemaVersion = 1;
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly string[] PasswordChangedFields = ["password"];
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IIdentityRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IOutboxAppender _outboxAppender;
    private readonly IVersionedPasswordHasher _passwordHasher;
    private readonly IPasswordResetTokenHasher _tokenHasher;
    private readonly IEmailSecretEnvelope _emailEnvelope;
    private readonly IPasswordResetRateLimiter _rateLimiter;
    private readonly IdentityPolicy _options;
    private readonly TimeProvider _timeProvider;

    internal IdentityUseCaseService(
        IIdentityRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IOutboxAppender outboxAppender,
        IVersionedPasswordHasher passwordHasher,
        IPasswordResetTokenHasher tokenHasher,
        IEmailSecretEnvelope emailEnvelope,
        IPasswordResetRateLimiter rateLimiter,
        IdentityPolicy options,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _outboxAppender = outboxAppender ?? throw new ArgumentNullException(nameof(outboxAppender));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _tokenHasher = tokenHasher ?? throw new ArgumentNullException(nameof(tokenHasher));
        _emailEnvelope = emailEnvelope ?? throw new ArgumentNullException(nameof(emailEnvelope));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<Result<UserPage>> ExecuteAsync(
        ListUsersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanReadUsers(query.Actor))
        {
            return Failure<UserPage>(IdentityErrorCodes.RoleRequired, "The actor role cannot read users.");
        }

        if (query.Limit is < 1 or > 100)
        {
            return Failure<UserPage>(
                IdentityErrorCodes.InvalidRequest,
                "The pagination request is invalid.");
        }

        if (!TryDecodeCursor(query.Cursor, out UserCursor? cursor))
        {
            return Failure<UserPage>(
                IdentityErrorCodes.InvalidRequest,
                "The pagination cursor is malformed.");
        }

        UserSlice slice = await _repository
            .ListAsync(cursor, query.Limit, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<UserView> users = slice.Users.Select(static user => user.ToView()).ToArray();
        string? nextCursor = slice.HasMore && slice.Users.Count > 0
            ? EncodeCursor(slice.Users[^1])
            : null;
        return Result.Success(new UserPage(users, nextCursor, slice.HasMore));
    }

    public async ValueTask<Result<UserView>> ExecuteAsync(
        GetUserQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!CanReadUsers(query.Actor))
        {
            return Failure<UserView>(IdentityErrorCodes.RoleRequired, "The actor role cannot read users.");
        }

        IdentityUser? user = await _repository
            .GetAsync(query.UserId, cancellationToken)
            .ConfigureAwait(false);
        return user is null
            ? Failure<UserView>(IdentityErrorCodes.ResourceNotFound, "The user does not exist.")
            : Result.Success(user.ToView());
    }

    public async ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdmin(command.Actor))
        {
            return Failure<IdentityCommandOutcome<UserView>>(
                IdentityErrorCodes.RoleRequired,
                "The admin role is required.");
        }

        string normalizedEmail;
        string displayName;
        string passwordHash;
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            normalizedEmail = IdentityInput.NormalizeEmail(command.Email);
            displayName = IdentityInput.DisplayName(command.DisplayName);
            IdentityInput.Password(command.TemporaryPassword, _options.PasswordMinimumLength);
            passwordHash = _passwordHasher.Hash(command.TemporaryPassword);
            requestHash = HashRequest(new
            {
                email = normalizedEmail,
                display_name = displayName,
                role = RoleCode(command.Role),
                temporary_password = command.TemporaryPassword,
            });
        }
        catch (ArgumentException exception)
        {
            return Failure<IdentityCommandOutcome<UserView>>(
                string.Equals(exception.ParamName, "password", StringComparison.Ordinal)
                    ? IdentityErrorCodes.PasswordPolicyFailed
                    : IdentityErrorCodes.ValidationFailed,
                "The create-user request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            CreateScope(command.Actor),
            command.IdempotencyKey,
            ActorFingerprint(command.Actor),
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome<UserView>>? early = ReplayOrAcquireFailure<UserView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        EntityId userId = EntityId.New();
        IdentityUser? user = await _repository.CreateAsync(
            userId,
            command.Email.Trim(),
            normalizedEmail,
            displayName,
            passwordHash,
            command.Role,
            command.Actor.UserId,
            EntityId.New(),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await CompleteFailureAsync<IdentityCommandOutcome<UserView>>(
                lease,
                409,
                IdentityErrorCodes.ResourceConflict,
                "The email identity already exists.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            command.Actor,
            "identity.user.created",
            user.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            before: null,
            after: UserAuditState(user),
            idempotencyKey: command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        EntityId createdEventId = EntityId.New();
        await AppendEventAsync(
            createdEventId,
            "user_created",
            user,
            command.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = EventSchemaVersion,
                event_id = createdEventId.Value,
                event_type = "user_created",
                user_id = user.Id.Value,
                role = RoleCode(user.Role),
                status = StatusCode(user.Status),
                version = user.Version,
                origin = "admin_api",
            }),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        UserView view = user.ToView();
        string etag = ETag(view.Version);
        string location = $"/api/v1/admin/users/{view.Id.Value:D}";
        IdentityCommandOutcome<UserView> outcome = new(201, false, view, etag, location);
        await CompleteSuccessAsync<UserView>(
            lease,
            201,
            view,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
                ["Location"] = location,
            }),
            "user",
            user.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(outcome);
    }

    public async ValueTask<Result<IdentityCommandOutcome<UserView>>> ExecuteAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdmin(command.Actor))
        {
            return Failure<IdentityCommandOutcome<UserView>>(
                IdentityErrorCodes.RoleRequired,
                "The admin role is required.");
        }

        string? displayName;
        string? reason;
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            if (command.ExpectedVersion <= 0
                || (command.DisplayName is null && command.Role is null && command.Status is null))
            {
                throw new ArgumentException(
                    "The update request is empty or has an invalid version.",
                    nameof(command));
            }

            displayName = command.DisplayName is null
                ? null
                : IdentityInput.DisplayName(command.DisplayName);
            reason = command.Role is not null || command.Status is not null
                ? IdentityInput.Reason(command.Reason ?? string.Empty)
                : command.Reason is null ? null : IdentityInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                user_id = command.UserId.Value,
                expected_version = command.ExpectedVersion,
                display_name = displayName,
                role = command.Role is null ? null : RoleCode(command.Role.Value),
                status = command.Status is null ? null : StatusCode(command.Status.Value),
                reason,
            });
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome<UserView>>(
                IdentityErrorCodes.ValidationFailed,
                "The update-user request is invalid.");
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            UpdateScope(command.Actor, command.UserId),
            command.IdempotencyKey,
            ActorFingerprint(command.Actor),
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome<UserView>>? early = ReplayOrAcquireFailure<UserView>(acquire);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        UpdateUserPersistenceResult update = await _repository.UpdateAsync(
            command.UserId,
            command.ExpectedVersion,
            displayName,
            command.Role,
            command.Status,
            command.Actor.UserId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (update.Disposition != UpdateUserDisposition.Updated)
        {
            (int status, string code, string description) = update.Disposition switch
            {
                UpdateUserDisposition.NotFound =>
                    (404, IdentityErrorCodes.ResourceNotFound, "The user does not exist."),
                UpdateUserDisposition.VersionConflict =>
                    (412, IdentityErrorCodes.VersionConflict, "The user version has changed."),
                UpdateUserDisposition.LastActiveAdminConflict =>
                    (409, IdentityErrorCodes.ResourceConflict, "The last active admin cannot be removed."),
                _ => throw new InvalidOperationException("Unknown user update disposition."),
            };
            string? currentETag = update.Disposition == UpdateUserDisposition.VersionConflict
                ? ETag(update.Before!.Version)
                : null;
            return await CompleteFailureAsync<IdentityCommandOutcome<UserView>>(
                lease,
                status,
                code,
                description,
                unitOfWork,
                cancellationToken,
                currentETag).ConfigureAwait(false);
        }

        IdentityUser user = update.User!;
        if (update.Changed)
        {
            await AppendAuditAsync(
                command.Actor,
                "identity.user.updated",
                user.Id,
                command.RequestId,
                reason,
                command.IpAddress,
                command.UserAgent,
                UserAuditState(update.Before!),
                UserAuditState(user),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            EntityId updatedEventId = EntityId.New();
            await AppendEventAsync(
                updatedEventId,
                "user_updated",
                user,
                command.RequestId,
                JsonSerializer.SerializeToElement(new
                {
                    schema_version = EventSchemaVersion,
                    event_id = updatedEventId.Value,
                    event_type = "user_updated",
                    user_id = user.Id.Value,
                    role = RoleCode(user.Role),
                    status = StatusCode(user.Status),
                    version = user.Version,
                    changed_fields = ChangedFields(update.Before!, user),
                    origin = "admin_api",
                }),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        UserView view = user.ToView();
        string etag = ETag(view.Version);
        IdentityCommandOutcome<UserView> outcome = new(200, false, view, etag);
        await CompleteSuccessAsync<UserView>(
            lease,
            200,
            view,
            JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            }),
            "user",
            user.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(outcome);
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        AdminPasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsAdmin(command.Actor))
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.RoleRequired,
                "The admin role is required.");
        }

        string reason;
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            reason = IdentityInput.Reason(command.Reason);
            requestHash = HashRequest(new { user_id = command.UserId.Value, reason });
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The password-reset request is invalid.");
        }

        string idempotencyScope = AdminResetScope(command.Actor, command.UserId);
        string actorFingerprint = ActorFingerprint(command.Actor);
        IUnitOfWork preflightUnitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (preflightUnitOfWork.ConfigureAwait(false))
        {
            CommandIdempotencyAcquireResult preflightAcquire = await AcquireAsync(
                idempotencyScope,
                command.IdempotencyKey,
                actorFingerprint,
                requestHash,
                command.RequestId,
                preflightUnitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<IdentityCommandOutcome>? preflightEarly = ReplayOrAcquireFailure(
                preflightAcquire,
                command.UserId);
            if (preflightEarly is not null)
            {
                return preflightEarly;
            }
        }

        IdentityUser? preflightUser = await _repository
            .GetAsync(command.UserId, cancellationToken)
            .ConfigureAwait(false);
        bool adminRateLimitChecked = preflightUser is not null;
        if (adminRateLimitChecked)
        {
            Result<IdentityCommandOutcome>? rateLimitFailure = await CheckAdminRateLimitAsync(
                preflightUser!.NormalizedEmail,
                cancellationToken).ConfigureAwait(false);
            if (rateLimitFailure is not null)
            {
                return rateLimitFailure;
            }
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            idempotencyScope,
            command.IdempotencyKey,
            actorFingerprint,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome>? early = ReplayOrAcquireFailure(
            acquire,
            command.UserId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease lease = acquire.Lease!;
        IdentityUser? user = await _repository.GetAsync(
            command.UserId,
            unitOfWork.Context,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await CompleteFailureAsync<IdentityCommandOutcome>(
                lease,
                404,
                IdentityErrorCodes.ResourceNotFound,
                "The user does not exist.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (user.Status != UserLifecycle.Active)
        {
            return await CompleteFailureAsync<IdentityCommandOutcome>(
                lease,
                409,
                IdentityErrorCodes.ResourceConflict,
                "A disabled user cannot receive a password-reset request.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (!adminRateLimitChecked)
        {
            return await CompleteFailureAsync<IdentityCommandOutcome>(
                lease,
                409,
                IdentityErrorCodes.ResourceConflict,
                "The user changed to active while the password-reset request was being prepared.",
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        await PersistPasswordResetRequestAsync(
            user,
            command.RequestId,
            command.Actor,
            reason,
            command.IpAddress,
            command.UserAgent,
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await CompleteSuccessAsync<object>(
            lease,
            202,
            body: null,
            EmptyObject,
            "user",
            user.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(202, false));
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        ForgotPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string normalizedEmail;
        try
        {
            normalizedEmail = IdentityInput.NormalizeEmail(command.Email);
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The password-reset request is invalid.");
        }

        Result<IdentityCommandOutcome>? rateLimitFailure = await CheckForgotRateLimitAsync(
            command.IpAddress ?? string.Empty,
            normalizedEmail,
            cancellationToken).ConfigureAwait(false);
        if (rateLimitFailure is not null)
        {
            return rateLimitFailure;
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        IdentityUser? user = await _repository.FindByNormalizedEmailAsync(
            normalizedEmail,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (user is not null && user.Status == UserLifecycle.Active)
        {
            await PersistPasswordResetRequestAsync(
                user,
                command.RequestId,
                actor: null,
                reason: null,
                command.IpAddress,
                command.UserAgent,
                idempotencyKey: null,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(202, false));
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        CompletePasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        IReadOnlyList<PasswordResetTokenCandidate> candidates;
        try
        {
            IdentityInput.Password(command.NewPassword, _options.PasswordMinimumLength);
            candidates = _tokenHasher.HashCandidates(command.Token);
        }
        catch (ArgumentException exception)
        {
            return Failure<IdentityCommandOutcome>(
                string.Equals(exception.ParamName, "password", StringComparison.Ordinal)
                    ? IdentityErrorCodes.PasswordPolicyFailed
                    : IdentityErrorCodes.ValidationFailed,
                "The password-reset completion request is invalid.");
        }

        if (candidates.Count == 0
            || !await _repository.HasConsumablePasswordResetAsync(
                candidates,
                cancellationToken).ConfigureAwait(false))
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.PasswordResetTokenInvalid,
                "The password-reset token is invalid or expired.");
        }

        string passwordHash = _passwordHasher.Hash(command.NewPassword);

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        PasswordResetConsumeResult? consumed = await _repository.ConsumePasswordResetAsync(
            candidates,
            passwordHash,
            EntityId.New(),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (consumed is null)
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.PasswordResetTokenInvalid,
                "The password-reset token is invalid or expired.");
        }

        IdentityUser user = consumed.User;
        await AppendAuditAsync(
            actor: null,
            "identity.password_reset.completed",
            user.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            before: null,
            after: JsonSerializer.SerializeToElement(new
            {
                user_id = user.Id.Value,
                version = user.Version,
            }),
            idempotencyKey: null,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        EntityId completedEventId = EntityId.New();
        await AppendEventAsync(
            completedEventId,
            "password_reset_completed",
            user,
            command.RequestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = EventSchemaVersion,
                event_id = completedEventId.Value,
                event_type = "password_reset_completed",
                user_id = user.Id.Value,
                password_reset_id = consumed.PasswordResetId.Value,
                version = user.Version,
                changed_fields = PasswordChangedFields,
                origin = "anonymous_api",
            }),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(204, false));
    }

    private async ValueTask PersistPasswordResetRequestAsync(
        IdentityUser user,
        EntityId requestId,
        IdentityActor? actor,
        string? reason,
        string? ipAddress,
        string? userAgent,
        string? idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        PasswordResetTokenSecret token = _tokenHasher.Create();
        EntityId tokenId = EntityId.New();
        EntityId emailOutboxId = EntityId.New();
        string resetUrl = _options.BuildPasswordResetUrl(token.Token);
        PasswordResetEmailEnvelopes envelopes = _emailEnvelope.Encrypt(
            new EmailSecretEnvelopePlaintext(user.Email, resetUrl),
            emailOutboxId);
        JsonElement templatePayload = JsonSerializer.SerializeToElement(new
        {
            expires_in_minutes = checked((int)_options.PasswordResetLifetime.TotalMinutes),
        });
        await _repository.InsertPasswordResetAsync(
            new PasswordResetOutboxWrite(
                tokenId,
                emailOutboxId,
                user.Id,
                token.Hash,
                token.PepperVersion,
                _options.PasswordResetLifetime,
                $"password-reset:{requestId.Value:D}",
                _options.BuildMessageId(emailOutboxId),
                envelopes.RecipientEnvelope,
                templatePayload,
                envelopes.DeliverySecretEnvelope),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        await AppendAuditAsync(
            actor,
            "identity.password_reset.requested",
            user.Id,
            requestId,
            reason,
            ipAddress,
            userAgent,
            before: actor is null ? null : UserAuditState(user),
            after: JsonSerializer.SerializeToElement(new
            {
                user_id = user.Id.Value,
                request_id = requestId.Value,
                reset_requested = true,
            }),
            idempotencyKey,
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
        EntityId requestedEventId = EntityId.New();
        await AppendEventAsync(
            requestedEventId,
            "password_reset_requested",
            user,
            requestId,
            JsonSerializer.SerializeToElement(new
            {
                schema_version = EventSchemaVersion,
                event_id = requestedEventId.Value,
                event_type = "password_reset_requested",
                user_id = user.Id.Value,
                password_reset_id = tokenId.Value,
                version = user.Version,
                origin = actor is null ? "anonymous_api" : "admin_api",
            }),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<IdentityCommandOutcome>?> CheckForgotRateLimitAsync(
        string ipAddress,
        string normalizedAccount,
        CancellationToken cancellationToken)
    {
        PasswordResetRateLimitDecision decision = await _rateLimiter.CheckForgotAsync(
            ipAddress,
            normalizedAccount,
            cancellationToken).ConfigureAwait(false);
        return RateLimitFailure(decision);
    }

    private async ValueTask<Result<IdentityCommandOutcome>?> CheckAdminRateLimitAsync(
        string normalizedAccount,
        CancellationToken cancellationToken)
    {
        PasswordResetRateLimitDecision decision = await _rateLimiter.CheckAdminAsync(
            normalizedAccount,
            cancellationToken).ConfigureAwait(false);
        return RateLimitFailure(decision);
    }

    private static Result<IdentityCommandOutcome>? RateLimitFailure(
        PasswordResetRateLimitDecision decision) => decision.Disposition switch
        {
            PasswordResetRateLimitDisposition.Allowed => null,
            PasswordResetRateLimitDisposition.Rejected => Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.RateLimitExceeded,
                "The password-reset rate limit was exceeded.",
                decision.RetryAfterSeconds),
            PasswordResetRateLimitDisposition.Unavailable => Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.CoordinationUnavailable,
                "Password-reset coordination is unavailable.",
                retryAfterSeconds: 1),
            _ => throw new InvalidOperationException(
                "Unknown password-reset rate-limit disposition."),
        };

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        string scope,
        string key,
        string actorFingerprint,
        byte[] requestHash,
        EntityId requestId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                scope,
                key,
                EntityId.New(),
                actorFingerprint,
                requestHash,
                requestId,
                IdempotencyLease,
                IdempotencyRetention),
            unitOfWorkContext,
            cancellationToken);

    private static Result<IdentityCommandOutcome<T>>? ReplayOrAcquireFailure<T>(
        CommandIdempotencyAcquireResult acquire)
    {
        return acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure<IdentityCommandOutcome<T>>(
                IdentityErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure<IdentityCommandOutcome<T>>(
                IdentityErrorCodes.CoordinationUnavailable,
                "The matching idempotent command is still in progress."),
            CommandIdempotencyDisposition.Replay => Replay<T>(acquire.Response!),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };
    }

    private static Result<IdentityCommandOutcome>? ReplayOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        EntityId expectedResourceId)
    {
        return acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.IdempotencyConflict,
                "The idempotency key was already used for a different request."),
            CommandIdempotencyDisposition.Busy => Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.CoordinationUnavailable,
                "The matching idempotent command is still in progress."),
            CommandIdempotencyDisposition.Replay => Replay(
                acquire.Response!,
                expectedResourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };
    }

    private static Result<IdentityCommandOutcome<T>> Replay<T>(CommandIdempotencyResponse response)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailure failure = ParseReplayFailure(response);
            string? failureETag = ValidateFailureReplay(response, failure);
            return Failure<IdentityCommandOutcome<T>>(
                failure.Presentation.Code,
                failure.Description,
                etag: failureETag,
                presentation: failure.Presentation);
        }

        if (response.Body is null)
        {
            throw new InvalidOperationException("Idempotency replay is missing its response body.");
        }

        T value = DeserializeReplayBody<T>(response.Body.Value);
        (string etag, string? location) = ValidateUserReplay(response, value);
        return Result.Success(new IdentityCommandOutcome<T>(
            response.Status,
            true,
            value,
            etag,
            location));
    }

    private static Result<IdentityCommandOutcome> Replay(
        CommandIdempotencyResponse response,
        EntityId expectedResourceId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            ReplayFailure failure = ParseReplayFailure(response);
            string? etag = ValidateFailureReplay(response, failure);
            return Failure<IdentityCommandOutcome>(
                failure.Presentation.Code,
                failure.Description,
                etag: etag,
                presentation: failure.Presentation);
        }

        if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status != 202
            || response.Body is not null
            || response.BodyEnvelope is not null
            || response.Headers.ValueKind != JsonValueKind.Object
            || response.Headers.EnumerateObject().Any()
            || !string.Equals(response.ResourceType, "user", StringComparison.Ordinal)
            || response.ResourceId != expectedResourceId)
        {
            throw new InvalidOperationException(
                "Admin password-reset idempotency replay is invalid.");
        }

        return Result.Success(new IdentityCommandOutcome(202, true));
    }

    private static ReplayFailure ParseReplayFailure(CommandIdempotencyResponse response) =>
        response.Body?.Deserialize<ReplayFailure>()
            ?? throw new InvalidOperationException("Idempotency failure replay body is invalid.");

    private static string? ValidateFailureReplay(
        CommandIdempotencyResponse response,
        ReplayFailure failure)
    {
        string? etag = Header(response.Headers, "ETag");
        ResultErrorPresentation presentation = failure.Presentation;
        ResultErrorPresentation expected = CreateFailurePresentation(
            presentation.Status,
            presentation.Code);
        bool isVersionConflict = string.Equals(
            presentation.Code,
            IdentityErrorCodes.VersionConflict,
            StringComparison.Ordinal);
        if (response.Status != presentation.Status
            || presentation != expected
            || isVersionConflict != (response.Status == 412)
            || isVersionConflict != (etag is not null)
            || etag is not null && !IsCanonicalETag(etag))
        {
            throw new InvalidOperationException(
                "Idempotency failure replay status or headers are invalid.");
        }

        return etag;
    }

    private static ResultErrorPresentation CreateFailurePresentation(
        int status,
        string code)
    {
        (string title, string detail, bool retryable) = (code, status) switch
        {
            (IdentityErrorCodes.ResourceNotFound, 404) =>
                ("Resource not found", "The requested resource was not found.", false),
            (IdentityErrorCodes.ResourceConflict, 409) =>
                ("Resource conflict", "The requested state conflicts with the current resource state.", false),
            (IdentityErrorCodes.VersionConflict, 412) =>
                ("Version conflict", "The resource version no longer matches; retrieve it again before retrying.", true),
            _ => throw new InvalidOperationException(
                "The idempotent failure code and status are not a supported pair."),
        };
        return new ResultErrorPresentation(
            code,
            status,
            title,
            detail,
            retryable);
    }

    private static string? Header(JsonElement headers, string name) =>
        headers.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;

    private static T DeserializeReplayBody<T>(JsonElement body)
    {
        if (typeof(T) != typeof(UserView))
        {
            return body.Deserialize<T>()
                ?? throw new InvalidOperationException("Idempotency replay body is invalid.");
        }

        UserViewReplay replay = body.Deserialize<UserViewReplay>()
            ?? throw new InvalidOperationException("User idempotency replay body is invalid.");
        UserView view = replay.ToUserView();
        return (T)(object)view;
    }

    private static (string ETag, string? Location) ValidateUserReplay<T>(
        CommandIdempotencyResponse response,
        T value)
    {
        if (value is not UserView user
            || response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
            || response.Status is not (200 or 201)
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "user", StringComparison.Ordinal)
            || response.ResourceId != user.Id
            || response.Headers.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "User idempotency replay status or headers are invalid.");
        }

        int expectedHeaderCount = response.Status == 201 ? 2 : 1;
        JsonProperty[] headers = response.Headers.EnumerateObject().ToArray();
        bool hasOnlyAllowedHeaders = headers.Length == expectedHeaderCount
            && headers.All(header =>
                header.Value.ValueKind == JsonValueKind.String
                && (string.Equals(header.Name, "ETag", StringComparison.Ordinal)
                    || response.Status == 201
                    && string.Equals(header.Name, "Location", StringComparison.Ordinal)));
        string? etag = Header(response.Headers, "ETag");
        string? location = response.Status == 201
            ? Header(response.Headers, "Location")
            : null;
        if (!hasOnlyAllowedHeaders
            || !string.Equals(etag, ETag(user.Version), StringComparison.Ordinal)
            || response.Status == 201
            && !string.Equals(
                location,
                $"/api/v1/admin/users/{user.Id.Value:D}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "User idempotency replay status or headers are invalid.");
        }

        return (etag!, location);
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
        ResultErrorPresentation presentation = CreateFailurePresentation(
            status,
            code);
        JsonElement body = JsonSerializer.SerializeToElement(
            new ReplayFailure(description, presentation));
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
                body,
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The idempotency lease was lost before completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(
            code,
            description,
            etag: etag,
            presentation: presentation);
    }

    private async ValueTask CompleteSuccessAsync<T>(
        CommandIdempotencyLease lease,
        int status,
        T? body,
        JsonElement headers,
        string? resourceType,
        EntityId? resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement? responseBody = body switch
        {
            null => null,
            UserView user => JsonSerializer.SerializeToElement(UserViewReplay.From(user)),
            _ => JsonSerializer.SerializeToElement(body),
        };
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                responseBody,
                ResponseBodyEnvelope: null,
                headers,
                resourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException("The idempotency lease was lost before completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        IdentityActor? actor,
        string action,
        EntityId targetId,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        JsonElement? before,
        JsonElement? after,
        string? idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                actor is null ? AuditActorType.System : AuditActorType.Admin,
                actor?.UserId,
                action,
                "user",
                targetId,
                requestId,
                reason,
                ipAddress,
                userAgent,
                before,
                after,
                AuditMetadata(idempotencyKey)),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);

    private JsonElement AuditMetadata(string? idempotencyKey) =>
        idempotencyKey is null
            ? EmptyObject
            : JsonSerializer.SerializeToElement(new
            {
                idempotency_key_hash = HmacText(
                    "poolai|audit-idempotency-key|identity|v1\0",
                    idempotencyKey),
            });

    private async ValueTask AppendEventAsync(
        EntityId eventId,
        string eventType,
        IdentityUser user,
        EntityId requestId,
        JsonElement payload,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => await _outboxAppender.AppendAsync(
            new IntegrationEvent(
                eventId,
                $"identity:{eventType}:{eventId.Value:D}",
                EventTopic,
                EventSchemaVersion,
                "user",
                user.Id,
                user.Version,
                eventType,
                SourceEventSequence: null,
                requestId,
                CausationId: null,
                payload,
                _timeProvider.GetUtcNow()),
            unitOfWorkContext,
            cancellationToken).ConfigureAwait(false);

    private static JsonElement UserAuditState(IdentityUser user) =>
        JsonSerializer.SerializeToElement(new
        {
            user_id = user.Id.Value,
            display_name = user.DisplayName,
            role = RoleCode(user.Role),
            status = StatusCode(user.Status),
            version = user.Version,
        });

    private static string[] ChangedFields(IdentityUser before, IdentityUser after)
    {
        List<string> fields = new(3);
        if (!string.Equals(before.DisplayName, after.DisplayName, StringComparison.Ordinal))
        {
            fields.Add("display_name");
        }

        if (before.Role != after.Role)
        {
            fields.Add("role");
        }

        if (before.Status != after.Status)
        {
            fields.Add("status");
        }

        return fields.ToArray();
    }

    private static bool IsAdmin(IdentityActor actor) =>
        actor.TokenVersion > 0 && actor.Role == SystemRole.Admin;

    private static bool CanReadUsers(IdentityActor actor) =>
        actor.TokenVersion > 0 && actor.Role is
            SystemRole.Admin or SystemRole.Operator or SystemRole.Auditor;

    private static string CreateScope(IdentityActor actor) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/admin/users";

    private static string UpdateScope(IdentityActor actor, EntityId userId) =>
        $"identity:{actor.UserId.Value:D}:patch:/api/v1/admin/users/{userId.Value:D}";

    private static string AdminResetScope(IdentityActor actor, EntityId userId) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/admin/users/{userId.Value:D}/password-reset";

    private static string ActorFingerprint(IdentityActor actor) =>
        $"user:{actor.UserId.Value:D}";

    private static string ETag(long version) => $"\"v{version}\"";

    private static bool IsCanonicalETag(string etag) =>
        etag.Length >= 4
        && etag[0] == '"'
        && etag[1] == 'v'
        && etag[^1] == '"'
        && long.TryParse(
            etag.AsSpan(2, etag.Length - 3),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private static string RoleCode(SystemRole role) => role switch
    {
        SystemRole.Admin => "admin",
        SystemRole.Operator => "operator",
        SystemRole.Auditor => "auditor",
        SystemRole.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string StatusCode(UserLifecycle status) => status switch
    {
        UserLifecycle.Active => "active",
        UserLifecycle.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
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
            return HMACSHA256.HashData(_options.RequestHashPepper, input);
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
                HMACSHA256.HashData(_options.RequestHashPepper, bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string EncodeCursor(IdentityUser user)
    {
        Span<byte> bytes = stackalloc byte[25];
        bytes[0] = 0x01;
        long unixMicroseconds = checked(
            (user.CreatedAt.UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks) / 10);
        BinaryPrimitives.WriteInt64BigEndian(bytes.Slice(1, 8), unixMicroseconds);
        Convert.FromHexString(user.Id.Value.ToString("N"), bytes[9..], out _, out _);
        return ToBase64Url(bytes);
    }

    private static bool TryDecodeCursor(string? encoded, out UserCursor? cursor)
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

            string base64 = encoded.Replace('-', '+').Replace('_', '/');
            base64 = (base64.Length % 4) switch
            {
                0 => base64,
                2 => base64 + "==",
                3 => base64 + "=",
                _ => string.Empty,
            };
            byte[] bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 25
                || bytes[0] != 0x01
                || !string.Equals(ToBase64Url(bytes), encoded, StringComparison.Ordinal))
            {
                return false;
            }

            long unixMicroseconds = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1, 8));
            long ticks = checked(
                DateTime.UnixEpoch.Ticks + checked(unixMicroseconds * 10));
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

            cursor = new UserCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new EntityId(id));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        string? etag = null,
        ResultErrorPresentation? presentation = null) =>
        Result.Failure<T>(
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

    private sealed record ReplayFailure(
        string Description,
        ResultErrorPresentation Presentation);

    private sealed record UserViewReplay(
        Guid Id,
        string Email,
        string DisplayName,
        SystemRole Role,
        UserLifecycle Status,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        internal static UserViewReplay From(UserView value) => new(
            value.Id.Value,
            value.Email,
            value.DisplayName,
            value.Role,
            value.Status,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt);

        internal UserView ToUserView()
        {
            if (Id == Guid.Empty
                || string.IsNullOrWhiteSpace(Email)
                || string.IsNullOrWhiteSpace(DisplayName)
                || Version <= 0)
            {
                throw new InvalidOperationException("User idempotency replay body is invalid.");
            }

            _ = RoleCode(Role);
            _ = StatusCode(Status);
            return new UserView(
                new EntityId(Id),
                Email,
                DisplayName,
                Role,
                Status,
                Version,
                CreatedAt,
                UpdatedAt);
        }
    }
}
#pragma warning restore MA0051
