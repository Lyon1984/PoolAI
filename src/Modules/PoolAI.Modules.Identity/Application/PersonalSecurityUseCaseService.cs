#pragma warning disable MA0051 // Security command handlers keep each transactional sequence explicit.
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed class PersonalSecurityUseCaseService :
    IChangePasswordUseCase,
    ISetupTotpUseCase,
    IConfirmTotpUseCase,
    IDisableTotpUseCase
{
    private const string TotpSetupResourceType = "totp_setup";
    private static readonly TimeSpan IdempotencyLease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdempotencyRetention = TimeSpan.FromHours(24);
    private static readonly JsonElement EmptyObject = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IIdentitySessionRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly ICommandIdempotencyStore _idempotencyStore;
    private readonly IAuditAppender _auditAppender;
    private readonly IVersionedPasswordHasher _passwordHasher;
    private readonly IOneTimeChallengeHasher _challengeHasher;
    private readonly ITotpAuthenticator _totpAuthenticator;
    private readonly ITotpSecretEnvelope _totpSecretEnvelope;
    private readonly ITotpRecoveryCodeGenerator _recoveryCodeGenerator;
    private readonly ITotpRecoveryCodeEnvelope _recoveryCodeEnvelope;
    private readonly ITotpSetupResponseEnvelope _setupResponseEnvelope;
    private readonly SessionPolicy _sessionPolicy;
    private readonly IdentityPolicy _identityPolicy;
    private readonly TimeProvider _timeProvider;

    internal PersonalSecurityUseCaseService(
        IIdentitySessionRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        ICommandIdempotencyStore idempotencyStore,
        IAuditAppender auditAppender,
        IVersionedPasswordHasher passwordHasher,
        IOneTimeChallengeHasher challengeHasher,
        ITotpAuthenticator totpAuthenticator,
        ITotpSecretEnvelope totpSecretEnvelope,
        ITotpRecoveryCodeGenerator recoveryCodeGenerator,
        ITotpRecoveryCodeEnvelope recoveryCodeEnvelope,
        ITotpSetupResponseEnvelope setupResponseEnvelope,
        SessionPolicy sessionPolicy,
        IdentityPolicy identityPolicy,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _idempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _challengeHasher = challengeHasher ?? throw new ArgumentNullException(nameof(challengeHasher));
        _totpAuthenticator = totpAuthenticator ?? throw new ArgumentNullException(nameof(totpAuthenticator));
        _totpSecretEnvelope = totpSecretEnvelope ?? throw new ArgumentNullException(nameof(totpSecretEnvelope));
        _recoveryCodeGenerator = recoveryCodeGenerator ?? throw new ArgumentNullException(nameof(recoveryCodeGenerator));
        _recoveryCodeEnvelope = recoveryCodeEnvelope ?? throw new ArgumentNullException(nameof(recoveryCodeEnvelope));
        _setupResponseEnvelope = setupResponseEnvelope ?? throw new ArgumentNullException(nameof(setupResponseEnvelope));
        _sessionPolicy = sessionPolicy ?? throw new ArgumentNullException(nameof(sessionPolicy));
        _identityPolicy = identityPolicy ?? throw new ArgumentNullException(nameof(identityPolicy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        ChangePasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string reason;
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            if (command.ExpectedVersion <= 0)
            {
                throw new ArgumentException("The expected version is invalid.", nameof(command));
            }

            ValidateCredentialText(command.CurrentPassword, nameof(command.CurrentPassword));
            IdentityInput.Password(command.NewPassword, _identityPolicy.PasswordMinimumLength);
            reason = IdentityInput.Reason(command.Reason);
            requestHash = HashRequest(new
            {
                user_id = command.Actor.UserId.Value,
                expected_version = command.ExpectedVersion,
                current_password = command.CurrentPassword,
                new_password = command.NewPassword,
                reason,
            });
        }
        catch (ArgumentException exception)
        {
            return Failure<IdentityCommandOutcome>(
                string.Equals(exception.ParamName, "password", StringComparison.Ordinal)
                    ? IdentityErrorCodes.PasswordPolicyFailed
                    : IdentityErrorCodes.ValidationFailed,
                "The password-change request is invalid.");
        }

        AuthenticationUserSnapshot? user = await ReadCurrentUserAsync(
            command.Actor,
            cancellationToken).ConfigureAwait(false);
        PreparedFailure? preparationFailure = null;
        string? passwordHash = null;
        if (user is null)
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidUserToken,
                "The access token no longer identifies an active current user.");
        }
        else if (!_passwordHasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidCredentials,
                "The current password is invalid.");
        }
        else
        {
            passwordHash = _passwordHasher.Hash(command.NewPassword);
        }

        string scope = PasswordScope(command.Actor);
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            scope,
            command.IdempotencyKey,
            command.Actor,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome>? early = ReplayNonBodyOrAcquireFailure(
            acquire,
            command.Actor.UserId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        if (preparationFailure is not null)
        {
            return await CompletePreparedFailureAsync<IdentityCommandOutcome>(
                idempotencyLease,
                preparationFailure,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        SecurityMutationPersistenceResult persisted = await _repository.ChangePasswordAsync(
            command.Actor.UserId,
            command.ExpectedVersion,
            user!.SecurityStamp,
            passwordHash!,
            EntityId.New(),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Disposition != SecurityMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<IdentityCommandOutcome>(
                idempotencyLease,
                persisted,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        AuthenticationUserSnapshot updated = persisted.User!;
        await AppendAuditAsync(
            command.Actor,
            "identity.password.changed",
            command.RequestId,
            reason,
            command.IpAddress,
            command.UserAgent,
            SecurityState(user!),
            SecurityState(updated),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(updated.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            204,
            responseEnvelope: null,
            Headers(etag),
            "user",
            updated.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(204, false, etag));
    }

    public async ValueTask<Result<IdentityCommandOutcome<TotpSetupView>>> ExecuteAsync(
        SetupTotpCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            ValidateCredentialText(command.CurrentPassword, nameof(command.CurrentPassword));
            requestHash = HashRequest(new
            {
                user_id = command.Actor.UserId.Value,
                current_password = command.CurrentPassword,
            });
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome<TotpSetupView>>(
                IdentityErrorCodes.ValidationFailed,
                "The TOTP setup request is invalid.");
        }

        string scope = SetupTotpScope(command.Actor);
        IdempotencySecretBinding secretBinding = new(
            command.Actor.UserId,
            scope,
            command.IdempotencyKey,
            requestHash);

        AuthenticationUserSnapshot? user = await ReadCurrentUserAsync(
            command.Actor,
            cancellationToken).ConfigureAwait(false);
        PreparedFailure? preparationFailure = null;
        OneTimeChallengeSecret? challenge = null;
        TotpSetupResponseSecret? responseSecret = null;
        JsonElement? responseEnvelope = null;
        TotpChallengeWrite? write = null;
        if (user is null)
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidUserToken,
                "The access token no longer identifies an active current user.");
        }
        else if (!_passwordHasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidCredentials,
                "The current password is invalid.");
        }
        else if (user.TotpEnabled)
        {
            preparationFailure = new(
                409,
                IdentityErrorCodes.TotpAlreadyEnabled,
                "TOTP is already enabled.");
        }
        else
        {
            challenge = _challengeHasher.Create();
            TotpProvisioningSecret provisioning =
                _totpAuthenticator.CreateProvisioningSecret(user.Email);
            JsonElement secretEnvelope = _totpSecretEnvelope.Encrypt(
                provisioning.Base32Secret,
                TotpSecretEnvelopeTarget.SetupChallenge,
                challenge.Challenge);
            responseSecret = new TotpSetupResponseSecret(
                challenge.Challenge,
                provisioning.Base32Secret,
                provisioning.OtpAuthUri,
                SessionPolicy.TotpSetupSeconds);
            responseEnvelope = _setupResponseEnvelope.Encrypt(
                responseSecret,
                challenge.Challenge,
                secretBinding);
            write = new TotpChallengeWrite(
                challenge.Challenge,
                "setup",
                challenge.Hash,
                challenge.PepperVersion,
                _sessionPolicy.TotpSetupLifetime,
                secretEnvelope,
                responseEnvelope,
                user.SecurityStamp,
                user.TokenVersion);
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            scope,
            command.IdempotencyKey,
            command.Actor,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome<TotpSetupView>>? early = ReplaySetupOrAcquireFailure(
            acquire,
            secretBinding);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        if (preparationFailure is not null)
        {
            return await CompletePreparedFailureAsync<IdentityCommandOutcome<TotpSetupView>>(
                idempotencyLease,
                preparationFailure,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        SecurityMutationPersistenceResult persisted = await _repository.CreateTotpSetupAsync(
            command.Actor.UserId,
            user!.SecurityStamp,
            write!,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Disposition != SecurityMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<IdentityCommandOutcome<TotpSetupView>>(
                idempotencyLease,
                persisted,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        await AppendAuditAsync(
            command.Actor,
            "identity.totp.setup_started",
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            SecurityState(user!),
            JsonSerializer.SerializeToElement(new
            {
                user!.Version,
                user.TokenVersion,
                TotpEnabled = false,
                SetupPending = true,
            }),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await CompleteSuccessAsync(
            idempotencyLease,
            200,
            responseEnvelope!.Value,
            EmptyObject,
            TotpSetupResourceType,
            challenge!.Challenge,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome<TotpSetupView>(
            200,
            false,
            SetupView(responseSecret!)));
    }

    public async ValueTask<Result<IdentityCommandOutcome<TotpConfirmView>>> ExecuteAsync(
        ConfirmTotpCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            if (command.ExpectedVersion <= 0 || !IsTotpCode(command.TotpCode))
            {
                throw new ArgumentException("The TOTP confirm request is invalid.", nameof(command));
            }

            requestHash = HashRequest(new
            {
                user_id = command.Actor.UserId.Value,
                expected_version = command.ExpectedVersion,
                challenge_id = command.ChallengeId.Value,
                totp_code = command.TotpCode,
            });
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome<TotpConfirmView>>(
                IdentityErrorCodes.ValidationFailed,
                "The TOTP confirm request is invalid.");
        }

        string scope = ConfirmTotpScope(command.Actor);
        IdempotencySecretBinding secretBinding = new(
            command.Actor.UserId,
            scope,
            command.IdempotencyKey,
            requestHash);

        IReadOnlyList<CredentialHashCandidate> challengeCandidates = ToCandidates(
            _challengeHasher.HashCandidates(command.ChallengeId));
        TotpChallengeSnapshot? challenge = await _repository.FindTotpChallengeAsync(
            challengeCandidates,
            "setup",
            cancellationToken).ConfigureAwait(false);
        PreparedFailure? preparationFailure = null;
        string[]? recoveryCodes = null;
        JsonElement? recoveryEnvelope = null;
        TotpConfirmWrite? write = null;
        if (challenge is null
            || challenge.UserId != command.Actor.UserId
            || challenge.User.Status != UserLifecycle.Active
            || challenge.User.TotpEnabled
            || challenge.SecurityStamp != challenge.User.SecurityStamp
            || challenge.TokenVersion != challenge.User.TokenVersion
            || challenge.User.TokenVersion != command.Actor.TokenVersion)
        {
            preparationFailure = new(
                409,
                IdentityErrorCodes.TotpSetupExpired,
                "The TOTP setup is invalid or expired.");
        }
        else
        {
            string base32Secret = _totpSecretEnvelope.Decrypt(
                challenge.SecretEnvelope
                    ?? throw new InvalidOperationException("The TOTP setup seed envelope is missing."),
                TotpSecretEnvelopeTarget.SetupChallenge,
                challenge.Id);
            if (!_totpAuthenticator.TryMatchStep(
                    base32Secret,
                    command.TotpCode,
                    _timeProvider.GetUtcNow(),
                    out long matchedStep))
            {
                preparationFailure = new(
                    401,
                    IdentityErrorCodes.TotpCodeInvalid,
                    "The TOTP code is invalid.");
            }
            else
            {
                IReadOnlyList<TotpRecoveryCodeSecret> recoverySecrets =
                    _recoveryCodeGenerator.CreateBatch();
                if (recoverySecrets.Count != 8)
                {
                    throw new InvalidOperationException(
                        "TOTP recovery-code generation did not return eight codes.");
                }

                recoveryCodes = recoverySecrets
                    .Select(static item => item.Code)
                    .ToArray();
                recoveryEnvelope = _recoveryCodeEnvelope.Encrypt(
                    recoveryCodes,
                    challenge.Id,
                    secretBinding);
                JsonElement userSecretEnvelope = _totpSecretEnvelope.Encrypt(
                    base32Secret,
                    TotpSecretEnvelopeTarget.User,
                    command.Actor.UserId);
                write = new TotpConfirmWrite(
                    command.Actor.UserId,
                    command.ExpectedVersion,
                    challengeCandidates,
                    matchedStep,
                    userSecretEnvelope,
                    recoveryEnvelope.Value,
                    recoverySecrets.Select(static item => new TotpRecoveryCodeWrite(
                        EntityId.New(),
                        item.Hash,
                        item.PepperVersion)).ToArray());
            }
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            scope,
            command.IdempotencyKey,
            command.Actor,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome<TotpConfirmView>>? early = ReplayConfirmOrAcquireFailure(
            acquire,
            secretBinding,
            command.ChallengeId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        if (preparationFailure is not null)
        {
            return await CompletePreparedFailureAsync<IdentityCommandOutcome<TotpConfirmView>>(
                idempotencyLease,
                preparationFailure,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        SecurityMutationPersistenceResult persisted = await _repository.ConfirmTotpAsync(
            write!,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Disposition != SecurityMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<IdentityCommandOutcome<TotpConfirmView>>(
                idempotencyLease,
                persisted,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        AuthenticationUserSnapshot updated = persisted.User!;
        await AppendAuditAsync(
            command.Actor,
            "identity.totp.enabled",
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            SecurityState(challenge!.User),
            SecurityState(updated),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(updated.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            200,
            recoveryEnvelope!.Value,
            Headers(etag),
            TotpSetupResourceType,
            challenge!.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome<TotpConfirmView>(
            200,
            false,
            new TotpConfirmView(recoveryCodes!),
            etag));
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        DisableTotpCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        byte[] requestHash;
        try
        {
            IdentityInput.IdempotencyKey(command.IdempotencyKey);
            if (command.ExpectedVersion <= 0 || !IsTotpCode(command.TotpCode))
            {
                throw new ArgumentException("The TOTP disable request is invalid.", nameof(command));
            }

            ValidateCredentialText(command.CurrentPassword, nameof(command.CurrentPassword));
            requestHash = HashRequest(new
            {
                user_id = command.Actor.UserId.Value,
                expected_version = command.ExpectedVersion,
                current_password = command.CurrentPassword,
                totp_code = command.TotpCode,
            });
        }
        catch (ArgumentException)
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The TOTP disable request is invalid.");
        }

        AuthenticationUserSnapshot? user = await ReadCurrentUserAsync(
            command.Actor,
            cancellationToken).ConfigureAwait(false);
        PreparedFailure? preparationFailure = null;
        long? matchedStep = null;
        if (user is null)
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidUserToken,
                "The access token no longer identifies an active current user.");
        }
        else if (!_passwordHasher.Verify(user.PasswordHash, command.CurrentPassword))
        {
            preparationFailure = new(
                401,
                IdentityErrorCodes.InvalidCredentials,
                "The current password is invalid.");
        }
        else if (!user.TotpEnabled)
        {
            preparationFailure = new(
                409,
                IdentityErrorCodes.TotpNotEnabled,
                "TOTP is not enabled.");
        }
        else
        {
            string base32Secret = _totpSecretEnvelope.Decrypt(
                user.TotpSecretEnvelope!.Value,
                TotpSecretEnvelopeTarget.User,
                user.Id);
            if (_totpAuthenticator.TryMatchStep(
                    base32Secret,
                    command.TotpCode,
                    _timeProvider.GetUtcNow(),
                    out long acceptedStep))
            {
                matchedStep = acceptedStep;
            }
            else
            {
                preparationFailure = new(
                    401,
                    IdentityErrorCodes.TotpCodeInvalid,
                    "The TOTP code is invalid.");
            }
        }

        string scope = DisableTotpScope(command.Actor);
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease = unitOfWork.ConfigureAwait(false);
        CommandIdempotencyAcquireResult acquire = await AcquireAsync(
            scope,
            command.IdempotencyKey,
            command.Actor,
            requestHash,
            command.RequestId,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        Result<IdentityCommandOutcome>? early = ReplayNonBodyOrAcquireFailure(
            acquire,
            command.Actor.UserId);
        if (early is not null)
        {
            return early;
        }

        CommandIdempotencyLease idempotencyLease = acquire.Lease!;
        if (preparationFailure is not null)
        {
            return await CompletePreparedFailureAsync<IdentityCommandOutcome>(
                idempotencyLease,
                preparationFailure,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        SecurityMutationPersistenceResult persisted = await _repository.DisableTotpAsync(
            command.Actor.UserId,
            command.ExpectedVersion,
            user!.SecurityStamp,
            matchedStep!.Value,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Disposition != SecurityMutationDisposition.Updated)
        {
            return await CompleteMutationFailureAsync<IdentityCommandOutcome>(
                idempotencyLease,
                persisted,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        AuthenticationUserSnapshot updated = persisted.User!;
        await AppendAuditAsync(
            command.Actor,
            "identity.totp.disabled",
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            SecurityState(user!),
            SecurityState(updated),
            command.IdempotencyKey,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        string etag = ETag(updated.Version);
        await CompleteSuccessAsync(
            idempotencyLease,
            204,
            responseEnvelope: null,
            Headers(etag),
            "user",
            updated.Id,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(204, false, etag));
    }

    private async ValueTask<AuthenticationUserSnapshot?> ReadCurrentUserAsync(
        SessionActor actor,
        CancellationToken cancellationToken)
    {
        AuthenticationUserSnapshot? user = await _repository.GetAuthenticationUserAsync(
            actor.UserId,
            cancellationToken).ConfigureAwait(false);
        return user is not null
            && user.Status == UserLifecycle.Active
            && user.TokenVersion == actor.TokenVersion
            ? user
            : null;
    }

    private ValueTask<CommandIdempotencyAcquireResult> AcquireAsync(
        string scope,
        string key,
        SessionActor actor,
        byte[] requestHash,
        EntityId requestId,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
            new CommandIdempotencyRequest(
                scope,
                key,
                EntityId.New(),
                ActorFingerprint(actor),
                requestHash,
                requestId,
                IdempotencyLease,
                IdempotencyRetention),
            context,
            cancellationToken);

    private Result<IdentityCommandOutcome<TotpSetupView>>? ReplaySetupOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        IdempotencySecretBinding binding) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => IdempotencyConflict<IdentityCommandOutcome<TotpSetupView>>(),
            CommandIdempotencyDisposition.Busy => IdempotencyBusy<IdentityCommandOutcome<TotpSetupView>>(),
            CommandIdempotencyDisposition.Replay => ReplaySetup(acquire.Response!, binding),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private Result<IdentityCommandOutcome<TotpConfirmView>>? ReplayConfirmOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        IdempotencySecretBinding binding,
        EntityId expectedResourceId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => IdempotencyConflict<IdentityCommandOutcome<TotpConfirmView>>(),
            CommandIdempotencyDisposition.Busy => IdempotencyBusy<IdentityCommandOutcome<TotpConfirmView>>(),
            CommandIdempotencyDisposition.Replay => ReplayConfirm(
                acquire.Response!,
                binding,
                expectedResourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private static Result<IdentityCommandOutcome>? ReplayNonBodyOrAcquireFailure(
        CommandIdempotencyAcquireResult acquire,
        EntityId expectedResourceId) => acquire.Disposition switch
        {
            CommandIdempotencyDisposition.Acquired => null,
            CommandIdempotencyDisposition.Conflict => IdempotencyConflict<IdentityCommandOutcome>(),
            CommandIdempotencyDisposition.Busy => IdempotencyBusy<IdentityCommandOutcome>(),
            CommandIdempotencyDisposition.Replay => ReplayNonBody(
                acquire.Response!,
                expectedResourceId),
            _ => throw new ArgumentOutOfRangeException(nameof(acquire)),
        };

    private Result<IdentityCommandOutcome<TotpSetupView>> ReplaySetup(
        CommandIdempotencyResponse response,
        IdempotencySecretBinding binding)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailureResult<IdentityCommandOutcome<TotpSetupView>>(response);
        }

        if (response.Status != 200
            || response.Body is not null
            || response.BodyEnvelope is null
            || response.ResourceId is null
            || !string.Equals(response.ResourceType, TotpSetupResourceType, StringComparison.Ordinal)
            || !HasNoHeaders(response.Headers))
        {
            throw new InvalidOperationException("The TOTP setup idempotency replay is invalid.");
        }

        TotpSetupResponseSecret secret = _setupResponseEnvelope.Decrypt(
            response.BodyEnvelope.Value,
            response.ResourceId.Value,
            binding);
        if (secret.Challenge != response.ResourceId.Value
            || secret.ExpiresInSeconds != SessionPolicy.TotpSetupSeconds)
        {
            throw new InvalidOperationException("The TOTP setup idempotency envelope is invalid.");
        }

        return Result.Success(new IdentityCommandOutcome<TotpSetupView>(
            200,
            true,
            SetupView(secret)));
    }

    private Result<IdentityCommandOutcome<TotpConfirmView>> ReplayConfirm(
        CommandIdempotencyResponse response,
        IdempotencySecretBinding binding,
        EntityId expectedResourceId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailureResult<IdentityCommandOutcome<TotpConfirmView>>(response);
        }

        string? etag = Header(response.Headers, "ETag");
        if (response.Status != 200
            || response.Body is not null
            || response.BodyEnvelope is null
            || response.ResourceId is null
            || response.ResourceId != expectedResourceId
            || !string.Equals(response.ResourceType, TotpSetupResourceType, StringComparison.Ordinal)
            || !HasOnlyETag(response.Headers, etag))
        {
            throw new InvalidOperationException("The TOTP confirm idempotency replay is invalid.");
        }

        IReadOnlyList<string> recoveryCodes = _recoveryCodeEnvelope.Decrypt(
            response.BodyEnvelope.Value,
            response.ResourceId.Value,
            binding);
        if (recoveryCodes.Count != 8)
        {
            throw new InvalidOperationException("The TOTP recovery-code replay is invalid.");
        }

        return Result.Success(new IdentityCommandOutcome<TotpConfirmView>(
            200,
            true,
            new TotpConfirmView(recoveryCodes),
            etag));
    }

    private static Result<IdentityCommandOutcome> ReplayNonBody(
        CommandIdempotencyResponse response,
        EntityId expectedResourceId)
    {
        if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
        {
            return ReplayFailureResult<IdentityCommandOutcome>(response);
        }

        string? etag = Header(response.Headers, "ETag");
        if (response.Status != 204
            || response.Body is not null
            || response.BodyEnvelope is not null
            || !string.Equals(response.ResourceType, "user", StringComparison.Ordinal)
            || response.ResourceId != expectedResourceId
            || !HasOnlyETag(response.Headers, etag))
        {
            throw new InvalidOperationException("The personal-security idempotency replay is invalid.");
        }

        return Result.Success(new IdentityCommandOutcome(204, true, etag));
    }

    private async ValueTask<Result<T>> CompleteMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        SecurityMutationPersistenceResult persisted,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        (int status, string code, string description, string? etag) = persisted.Disposition switch
        {
            SecurityMutationDisposition.NotFound => (
                401,
                IdentityErrorCodes.InvalidUserToken,
                "The access token no longer identifies a user.",
                null),
            SecurityMutationDisposition.VersionConflict => (
                412,
                IdentityErrorCodes.VersionConflict,
                "The user version has changed.",
                ETag(persisted.CurrentVersion ?? persisted.User!.Version)),
            SecurityMutationDisposition.InvalidCredential => (
                401,
                IdentityErrorCodes.InvalidCredentials,
                "The current security credential is no longer valid.",
                null),
            SecurityMutationDisposition.TotpAlreadyEnabled => (
                409,
                IdentityErrorCodes.TotpAlreadyEnabled,
                "TOTP is already enabled.",
                null),
            SecurityMutationDisposition.TotpNotEnabled => (
                409,
                IdentityErrorCodes.TotpNotEnabled,
                "TOTP is not enabled.",
                null),
            SecurityMutationDisposition.ChallengeInvalid or SecurityMutationDisposition.ChallengeExpired => (
                409,
                IdentityErrorCodes.TotpSetupExpired,
                "The TOTP setup is invalid or expired.",
                null),
            SecurityMutationDisposition.TotpReplay => (
                401,
                IdentityErrorCodes.TotpCodeInvalid,
                "The TOTP step has already been accepted.",
                null),
            _ => throw new InvalidOperationException("Unknown security-mutation disposition."),
        };
        return await CompleteFailureAsync<T>(
            lease,
            status,
            code,
            description,
            etag,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<Result<T>> CompletePreparedFailureAsync<T>(
        CommandIdempotencyLease lease,
        PreparedFailure failure,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken) => CompleteFailureAsync<T>(
            lease,
            failure.Status,
            failure.Code,
            failure.Description,
            failure.ETag,
            unitOfWork,
            cancellationToken);

    private async ValueTask<Result<T>> CompleteFailureAsync<T>(
        CommandIdempotencyLease lease,
        int status,
        string code,
        string description,
        string? etag,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        JsonElement headers = etag is null ? EmptyObject : Headers(etag);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                status,
                JsonSerializer.SerializeToElement(new ReplayFailure(code, description)),
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(completed);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Failure<T>(code, description, etag);
    }

    private async ValueTask CompleteSuccessAsync(
        CommandIdempotencyLease lease,
        int status,
        JsonElement? responseEnvelope,
        JsonElement headers,
        string resourceType,
        EntityId resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                ResponseBody: null,
                responseEnvelope,
                headers,
                resourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        EnsureCompleted(completed);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AppendAuditAsync(
        SessionActor actor,
        string action,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        JsonElement? before,
        JsonElement? after,
        string idempotencyKey,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                ActorType(actor.Role),
                actor.UserId,
                action,
                "user",
                actor.UserId,
                requestId,
                reason,
                ipAddress,
                userAgent,
                before,
                after,
                JsonSerializer.SerializeToElement(new
                {
                    idempotency_key_hash = HmacText(
                        "poolai|audit-idempotency-key|identity|v1\0",
                        idempotencyKey),
                })),
            context,
            cancellationToken).ConfigureAwait(false);

    private static AuditActorType ActorType(SystemRole role) => role switch
    {
        SystemRole.Admin => AuditActorType.Admin,
        SystemRole.Operator => AuditActorType.Operator,
        SystemRole.Auditor => AuditActorType.Auditor,
        SystemRole.User => AuditActorType.User,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static JsonElement SecurityState(AuthenticationUserSnapshot user) =>
        JsonSerializer.SerializeToElement(new
        {
            user.Version,
            user.TokenVersion,
            user.TotpEnabled,
        });

    private byte[] HashRequest<T>(T request)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request);
        byte[] domain = Encoding.UTF8.GetBytes("poolai|idempotency-request-hash|identity|v1\0");
        byte[] input = new byte[domain.Length + payload.Length];
        try
        {
            domain.CopyTo(input, 0);
            payload.CopyTo(input, domain.Length);
            return HMACSHA256.HashData(_identityPolicy.RequestHashPepper, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private string HmacText(string domain, string value)
    {
        byte[] input = Encoding.UTF8.GetBytes(domain + value);
        try
        {
            return Convert.ToHexStringLower(
                HMACSHA256.HashData(_identityPolicy.RequestHashPepper, input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static Result<T> ReplayFailureResult<T>(CommandIdempotencyResponse response)
    {
        ReplayFailure replay = response.Body?.Deserialize<ReplayFailure>()
            ?? throw new InvalidOperationException("The idempotency failure replay body is invalid.");
        string? etag = Header(response.Headers, "ETag");
        if (response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || response.Status != FailureStatus(replay.Code)
            || etag is null != !string.Equals(
                replay.Code,
                IdentityErrorCodes.VersionConflict,
                StringComparison.Ordinal)
            || etag is not null && !HasOnlyETag(response.Headers, etag)
            || etag is null && !HasNoHeaders(response.Headers))
        {
            throw new InvalidOperationException("The idempotency failure replay is invalid.");
        }

        return Failure<T>(replay.Code, replay.Description, etag);
    }

    private static int FailureStatus(string code) => code switch
    {
        IdentityErrorCodes.InvalidCredentials or
        IdentityErrorCodes.InvalidUserToken or
        IdentityErrorCodes.TotpCodeInvalid => 401,
        IdentityErrorCodes.TotpAlreadyEnabled or
        IdentityErrorCodes.TotpNotEnabled or
        IdentityErrorCodes.TotpSetupExpired => 409,
        IdentityErrorCodes.VersionConflict => 412,
        _ => throw new InvalidOperationException("The idempotent failure code is unsupported."),
    };

    private static string PasswordScope(SessionActor actor) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/me/password";

    private static string SetupTotpScope(SessionActor actor) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/me/totp/setup";

    private static string ConfirmTotpScope(SessionActor actor) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/me/totp/confirm";

    private static string DisableTotpScope(SessionActor actor) =>
        $"identity:{actor.UserId.Value:D}:post:/api/v1/me/totp/disable";

    private static string ActorFingerprint(SessionActor actor) =>
        $"user:{actor.UserId.Value:D}";

    private static string ETag(long version) => $"\"v{version}\"";

    private static JsonElement Headers(string etag) => JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ETag"] = etag,
        });

    private static string? Header(JsonElement headers, string name) =>
        headers.ValueKind == JsonValueKind.Object
        && headers.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;

    private static bool HasNoHeaders(JsonElement headers) =>
        headers.ValueKind == JsonValueKind.Object && !headers.EnumerateObject().Any();

    private static bool HasOnlyETag(JsonElement headers, string? etag) =>
        etag is not null
        && headers.ValueKind == JsonValueKind.Object
        && headers.EnumerateObject().Count() == 1
        && IsCanonicalETag(etag);

    private static bool IsCanonicalETag(string etag) => etag.Length >= 4
        && etag[0] == '"'
        && etag[1] == 'v'
        && etag[^1] == '"'
        && long.TryParse(
            etag.AsSpan(2, etag.Length - 3),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out long version)
        && version > 0;

    private static TotpSetupView SetupView(TotpSetupResponseSecret secret) => new(
        secret.Challenge,
        secret.Base32Secret,
        secret.OtpAuthUri,
        secret.ExpiresInSeconds);

    private static CredentialHashCandidate[] ToCandidates(
        IReadOnlyList<OneTimeChallengeCandidate> candidates) => candidates
        .Select(static candidate => new CredentialHashCandidate(
            candidate.Hash,
            candidate.PepperVersion))
        .ToArray();

    private static void ValidateCredentialText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length is < 1 or > 1024)
        {
            throw new ArgumentException("The credential length is invalid.", parameterName);
        }
    }

    private static bool IsTotpCode(string? value) => value is not null
        && value.Length == 6
        && value.All(static character => character is >= '0' and <= '9');

    private static Result<T> IdempotencyConflict<T>() => Failure<T>(
        IdentityErrorCodes.IdempotencyConflict,
        "The idempotency key was already used for a different request.");

    private static Result<T> IdempotencyBusy<T>() => Failure<T>(
        IdentityErrorCodes.CoordinationUnavailable,
        "The matching idempotent command is still in progress.");

    private static Result<T> Failure<T>(
        string code,
        string description,
        string? etag = null) => Result.Failure<T>(code, description, etag: etag);

    private static void EnsureCompleted(bool completed)
    {
        if (!completed)
        {
            throw new InvalidOperationException("The idempotency lease was lost before completion.");
        }
    }

    private sealed record PreparedFailure(
        int Status,
        string Code,
        string Description,
        string? ETag = null);

    private sealed record ReplayFailure(string Code, string Description);
}
#pragma warning restore MA0051
