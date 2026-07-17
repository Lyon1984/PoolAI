#pragma warning disable MA0051 // Authentication handlers keep each transaction boundary explicit.
using System.Runtime.CompilerServices;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed class SessionAuthenticationUseCaseService :
    ILoginUseCase,
    IVerifyLoginTotpUseCase,
    IRefreshSessionUseCase,
    ILogoutUseCase,
    IGetCurrentUserUseCase,
    IAccessSessionValidator
{
    private static readonly JsonElement EmptyMetadata = JsonSerializer.SerializeToElement(
        new Dictionary<string, string>(StringComparer.Ordinal));
    private static readonly EntityId UnknownLoginUserId = new(Guid.Parse(
        "ffffffff-ffff-4fff-bfff-ffffffffffff"));
    private static readonly EntityId UnknownLoginSecurityStamp = new(Guid.Parse(
        "ffffffff-ffff-4fff-bfff-fffffffffffe"));

    private readonly IIdentitySessionRepository _repository;
    private readonly IUnitOfWorkFactory _unitOfWorkFactory;
    private readonly IAuditAppender _auditAppender;
    private readonly IVersionedPasswordHasher _passwordHasher;
    private readonly IRefreshCredentialHasher _refreshHasher;
    private readonly IOneTimeChallengeHasher _challengeHasher;
    private readonly ITotpAuthenticator _totpAuthenticator;
    private readonly ITotpSecretEnvelope _totpSecretEnvelope;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly ILoginFailureRateLimiter _rateLimiter;
    private readonly SessionPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly string _dummyPasswordHash;

    internal SessionAuthenticationUseCaseService(
        IIdentitySessionRepository repository,
        IUnitOfWorkFactory unitOfWorkFactory,
        IAuditAppender auditAppender,
        IVersionedPasswordHasher passwordHasher,
        IRefreshCredentialHasher refreshHasher,
        IOneTimeChallengeHasher challengeHasher,
        ITotpAuthenticator totpAuthenticator,
        ITotpSecretEnvelope totpSecretEnvelope,
        IAccessTokenIssuer accessTokenIssuer,
        ILoginFailureRateLimiter rateLimiter,
        SessionPolicy policy,
        TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWorkFactory = unitOfWorkFactory ?? throw new ArgumentNullException(nameof(unitOfWorkFactory));
        _auditAppender = auditAppender ?? throw new ArgumentNullException(nameof(auditAppender));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _refreshHasher = refreshHasher ?? throw new ArgumentNullException(nameof(refreshHasher));
        _challengeHasher = challengeHasher ?? throw new ArgumentNullException(nameof(challengeHasher));
        _totpAuthenticator = totpAuthenticator ?? throw new ArgumentNullException(nameof(totpAuthenticator));
        _totpSecretEnvelope = totpSecretEnvelope ?? throw new ArgumentNullException(nameof(totpSecretEnvelope));
        _accessTokenIssuer = accessTokenIssuer ?? throw new ArgumentNullException(nameof(accessTokenIssuer));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dummyPasswordHash = _passwordHasher.Hash(
            "PoolAI-dummy-password-verification-material-v1-2026!");
    }

    public async ValueTask<Result<LoginResultView>> ExecuteAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        string normalizedEmail;
        try
        {
            normalizedEmail = IdentityInput.NormalizeEmail(command.Email);
            ValidateCredentialText(command.Password, nameof(command.Password));
        }
        catch (ArgumentException)
        {
            return Failure<LoginResultView>(
                IdentityErrorCodes.ValidationFailed,
                "The login request is invalid.");
        }

        AuthenticationUserSnapshot? preflight = await _repository
            .FindAuthenticationUserAsync(normalizedEmail, cancellationToken)
            .ConfigureAwait(false);
        bool passwordMatches = _passwordHasher.Verify(
            preflight?.PasswordHash ?? _dummyPasswordHash,
            command.Password);
        if (preflight is null || !passwordMatches)
        {
            Result<LoginResultView>? rateFailure = await RecordFailureAsync<LoginResultView>(
                command.IpAddress,
                cancellationToken).ConfigureAwait(false);
            if (rateFailure is not null)
            {
                return rateFailure;
            }

            await RecordRejectedPasswordAsync(
                preflight,
                command,
                cancellationToken).ConfigureAwait(false);
            return Failure<LoginResultView>(
                IdentityErrorCodes.InvalidCredentials,
                "The email or password is invalid.");
        }

        RefreshCredentialSecret? refresh = null;
        OneTimeChallengeSecret? challenge = null;
        PasswordLoginWrite write;
        if (preflight.TotpEnabled)
        {
            challenge = _challengeHasher.Create();
            write = new PasswordLoginWrite(
                preflight.Id,
                preflight.SecurityStamp,
                Session: null,
                new TotpChallengeWrite(
                    challenge.Challenge,
                    "login",
                    challenge.Hash,
                    challenge.PepperVersion,
                    _policy.LoginMfaChallengeLifetime,
                    SecretEnvelope: null,
                    ResponseBodyEnvelope: null,
                    preflight.SecurityStamp,
                    preflight.TokenVersion));
        }
        else
        {
            refresh = _refreshHasher.Create();
            write = new PasswordLoginWrite(
                preflight.Id,
                preflight.SecurityStamp,
                new RefreshSessionWrite(
                    EntityId.New(),
                    refresh.Hash,
                    refresh.PepperVersion,
                    _policy.RefreshLifetime,
                    command.IpAddress,
                    command.UserAgent),
                Challenge: null);
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        PasswordLoginPersistenceResult persisted = await _repository
            .CompletePasswordLoginAsync(write, unitOfWork.Context, cancellationToken)
            .ConfigureAwait(false);
        switch (persisted.Disposition)
        {
            case PasswordLoginDisposition.SessionCreated:
            {
                TokenPairView pair = IssueTokenPair(
                    persisted.User!,
                    persisted.SessionFamilyId!.Value,
                    refresh!);
                await AppendAuditAsync(
                    actor: null,
                    "identity.login.succeeded",
                    persisted.User!.Id,
                    command.RequestId,
                    reason: null,
                    command.IpAddress,
                    command.UserAgent,
                    "session_created",
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success<LoginResultView>(new LoginTokenResultView(pair));
            }

            case PasswordLoginDisposition.MfaRequired:
                await AppendAuditAsync(
                    actor: null,
                    "identity.login.mfa_required",
                    persisted.User!.Id,
                    command.RequestId,
                    reason: null,
                    command.IpAddress,
                    command.UserAgent,
                    "mfa_required",
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success<LoginResultView>(new LoginMfaResultView(
                    challenge!.Challenge,
                    SessionPolicy.LoginMfaChallengeSeconds));

            case PasswordLoginDisposition.AccountLocked:
                await AppendAuditAsync(
                    actor: null,
                    "identity.login.locked",
                    persisted.User!.Id,
                    command.RequestId,
                    reason: null,
                    command.IpAddress,
                    command.UserAgent,
                    "account_locked",
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<LoginResultView>(
                    IdentityErrorCodes.AccountLocked,
                    "The account is temporarily locked.",
                    persisted.RetryAfterSeconds);

            case PasswordLoginDisposition.UserDisabled:
                await AppendAuditAsync(
                    actor: null,
                    "identity.login.disabled",
                    preflight.Id,
                    command.RequestId,
                    reason: null,
                    command.IpAddress,
                    command.UserAgent,
                    "user_disabled",
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Failure<LoginResultView>(
                    IdentityErrorCodes.UserDisabled,
                    "The user is disabled.");

            case PasswordLoginDisposition.StaleCredential:
                return Failure<LoginResultView>(
                    IdentityErrorCodes.InvalidCredentials,
                    "The email or password is invalid.");

            default:
                throw new InvalidOperationException("Unknown password-login disposition.");
        }
    }

    public async ValueTask<Result<TokenPairView>> ExecuteAsync(
        VerifyLoginTotpCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsTotpCode(command.TotpCode))
        {
            return Failure<TokenPairView>(
                IdentityErrorCodes.ValidationFailed,
                "The TOTP verification request is invalid.");
        }

        IReadOnlyList<OneTimeChallengeCandidate> securityCandidates =
            _challengeHasher.HashCandidates(command.ChallengeId);
        IReadOnlyList<CredentialHashCandidate> candidates = ToCandidates(securityCandidates);
        TotpChallengeSnapshot? challenge = await _repository
            .FindTotpChallengeAsync(candidates, "login", cancellationToken)
            .ConfigureAwait(false);
        if (challenge is null
            || challenge.User.Status != UserLifecycle.Active
            || !challenge.User.TotpEnabled
            || challenge.SecurityStamp != challenge.User.SecurityStamp
            || challenge.TokenVersion != challenge.User.TokenVersion)
        {
            return await LoginTotpFailureAsync<TokenPairView>(
                IdentityErrorCodes.MfaChallengeInvalid,
                "The MFA challenge is invalid or expired.",
                command,
                targetId: null,
                action: "identity.login.mfa_rejected",
                outcome: "challenge_invalid",
                cancellationToken).ConfigureAwait(false);
        }

        string secret = _totpSecretEnvelope.Decrypt(
            challenge.User.TotpSecretEnvelope!.Value,
            TotpSecretEnvelopeTarget.User,
            challenge.User.Id);
        if (!_totpAuthenticator.TryMatchStep(
                secret,
                command.TotpCode,
                _timeProvider.GetUtcNow(),
                out long matchedStep))
        {
            return await LoginTotpFailureAsync<TokenPairView>(
                IdentityErrorCodes.TotpCodeInvalid,
                "The TOTP code is invalid.",
                command,
                challenge.User.Id,
                "identity.login.mfa_rejected",
                "totp_code_invalid",
                cancellationToken).ConfigureAwait(false);
        }

        if (challenge.User.TotpLastAcceptedStep is long lastAcceptedStep
            && matchedStep <= lastAcceptedStep)
        {
            return await LoginTotpFailureAsync<TokenPairView>(
                IdentityErrorCodes.TotpCodeInvalid,
                "The TOTP code has already been accepted.",
                command,
                challenge.User.Id,
                "identity.login.totp_replayed",
                "totp_replay",
                cancellationToken).ConfigureAwait(false);
        }

        RefreshCredentialSecret refresh = _refreshHasher.Create();
        RefreshSessionWrite sessionWrite = new(
            EntityId.New(),
            refresh.Hash,
            refresh.PepperVersion,
            _policy.RefreshLifetime,
            command.IpAddress,
            command.UserAgent);
        MfaLoginPersistenceResult persisted;
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (unitOfWork.ConfigureAwait(false))
        {
            persisted = await _repository.CompleteMfaLoginAsync(
                candidates,
                matchedStep,
                sessionWrite,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (persisted.Disposition == MfaLoginDisposition.SessionCreated)
            {
                TokenPairView pair = IssueTokenPair(
                    persisted.User!,
                    persisted.SessionFamilyId!.Value,
                    refresh);
                await AppendAuditAsync(
                    actor: null,
                    "identity.login.mfa_succeeded",
                    persisted.User!.Id,
                    command.RequestId,
                    reason: null,
                    command.IpAddress,
                    command.UserAgent,
                    "session_created",
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Success(pair);
            }

        }

        return persisted.Disposition switch
        {
            MfaLoginDisposition.ChallengeInvalid => await LoginTotpFailureAsync<TokenPairView>(
                    IdentityErrorCodes.MfaChallengeInvalid,
                    "The MFA challenge is invalid or expired.",
                    command,
                    challenge.User.Id,
                    "identity.login.mfa_rejected",
                    "challenge_invalid",
                    cancellationToken).ConfigureAwait(false),
            MfaLoginDisposition.TotpReplay => await LoginTotpFailureAsync<TokenPairView>(
                    IdentityErrorCodes.TotpCodeInvalid,
                    "The TOTP code has already been accepted.",
                    command,
                    challenge.User.Id,
                    "identity.login.totp_replayed",
                    "totp_replay",
                    cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unknown MFA-login disposition."),
        };
    }

    public async ValueTask<Result<TokenPairView>> ExecuteAsync(
        RefreshSessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        IReadOnlyList<RefreshCredentialCandidate> securityCandidates =
            _refreshHasher.HashCandidates(command.RefreshToken);
        if (securityCandidates.Count == 0)
        {
            return Failure<TokenPairView>(
                IdentityErrorCodes.RefreshTokenInvalid,
                "The refresh token is invalid or expired.");
        }

        CredentialHashCandidate[] candidates = ToCandidates(securityCandidates);
        if (!await _repository.HasRefreshCredentialAsync(candidates, cancellationToken)
                .ConfigureAwait(false))
        {
            return Failure<TokenPairView>(
                IdentityErrorCodes.RefreshTokenInvalid,
                "The refresh token is invalid or expired.");
        }

        RefreshCredentialSecret replacement = _refreshHasher.Create();
        RefreshSessionWrite write = new(
            EntityId.New(),
            replacement.Hash,
            replacement.PepperVersion,
            _policy.RefreshLifetime,
            command.IpAddress,
            command.UserAgent);
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        RefreshRotationPersistenceResult persisted = await _repository
            .RotateRefreshSessionAsync(
                candidates,
                write,
                unitOfWork.Context,
                cancellationToken)
            .ConfigureAwait(false);
        if (persisted.Disposition == RefreshRotationDisposition.Rotated)
        {
            TokenPairView pair = IssueTokenPair(
                persisted.User!,
                persisted.SessionFamilyId!.Value,
                replacement);
            await AppendAuditAsync(
                actor: null,
                "identity.refresh.rotated",
                persisted.User!.Id,
                command.RequestId,
                reason: null,
                command.IpAddress,
                command.UserAgent,
                "rotated",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(pair);
        }

        if (persisted.Disposition == RefreshRotationDisposition.Reused)
        {
            await AppendAuditAsync(
                actor: null,
                "identity.refresh.reused",
                persisted.User!.Id,
                command.RequestId,
                reason: null,
                command.IpAddress,
                command.UserAgent,
                "reused",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return persisted.Disposition == RefreshRotationDisposition.Reused
            ? Failure<TokenPairView>(
                IdentityErrorCodes.RefreshTokenReused,
                "The rotated refresh token was reused and its family was revoked.")
            : Failure<TokenPairView>(
                IdentityErrorCodes.RefreshTokenInvalid,
                "The refresh token is invalid or expired.");
    }

    public async ValueTask<Result<IdentityCommandOutcome>> ExecuteAsync(
        LogoutCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.AllSessions && command.RefreshToken is not null)
        {
            return Failure<IdentityCommandOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "refresh_token must be omitted when all_sessions is true.");
        }

        IReadOnlyList<CredentialHashCandidate> candidates = Array.Empty<CredentialHashCandidate>();
        if (command.RefreshToken is not null)
        {
            IReadOnlyList<RefreshCredentialCandidate> parsed =
                _refreshHasher.HashCandidates(command.RefreshToken);
            if (parsed.Count == 0)
            {
                return Result.Success(new IdentityCommandOutcome(204, false));
            }

            candidates = ToCandidates(parsed);
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        LogoutPersistenceResult persisted = await _repository.LogoutAsync(
            command.Actor,
            candidates,
            command.AllSessions,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (persisted.Changed)
        {
            await AppendAuditAsync(
                command.Actor,
                command.AllSessions ? "identity.logout.all" : "identity.logout.current",
                command.Actor.UserId,
                command.RequestId,
                reason: null,
                command.IpAddress,
                command.UserAgent,
                command.AllSessions ? "all_sessions_revoked" : "family_revoked",
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success(new IdentityCommandOutcome(204, false));
    }

    public async ValueTask<Result<CurrentUserView>> ExecuteAsync(
        GetCurrentUserQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        AuthenticationUserSnapshot? user = await _repository
            .GetAuthenticationUserAsync(query.Actor.UserId, cancellationToken)
            .ConfigureAwait(false);
        return user is null
            ? Failure<CurrentUserView>(
                IdentityErrorCodes.InvalidUserToken,
                "The user access token no longer identifies a user.")
            : Result.Success(user.ToCurrentUserView());
    }

    public async ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
        Guid userId,
        Guid sessionFamilyId,
        long tokenVersion,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty || sessionFamilyId == Guid.Empty || tokenVersion <= 0)
        {
            return null;
        }

        return await _repository.ReadCanonicalAuthorizationAsync(
            new EntityId(userId),
            new EntityId(sessionFamilyId),
            tokenVersion,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RecordRejectedPasswordAsync(
        AuthenticationUserSnapshot? user,
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        _ = await _repository.RecordPasswordFailureAsync(
            user?.Id ?? UnknownLoginUserId,
            user?.SecurityStamp ?? UnknownLoginSecurityStamp,
            _policy.MaximumPasswordFailures,
            _policy.LockoutDuration,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);

        await AppendAuditAsync(
            actor: null,
            "identity.login.rejected",
            user?.Id,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            "invalid_credentials",
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<T>?> RecordFailureAsync<T>(
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        LoginFailureRateLimitDecision decision = await _rateLimiter.RecordFailureAsync(
            ipAddress ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        return decision.Disposition switch
        {
            LoginFailureRateLimitDisposition.Allowed => null,
            LoginFailureRateLimitDisposition.Rejected => Failure<T>(
                IdentityErrorCodes.RateLimitExceeded,
                "The login failure limit was exceeded.",
                decision.RetryAfterSeconds),
            LoginFailureRateLimitDisposition.Unavailable => Failure<T>(
                IdentityErrorCodes.CoordinationUnavailable,
                "Login failure coordination is unavailable.",
                decision.RetryAfterSeconds ?? 1),
            _ => throw new InvalidOperationException("Unknown login rate-limit disposition."),
        };
    }

    private async ValueTask<Result<T>> LoginTotpFailureAsync<T>(
        string code,
        string description,
        VerifyLoginTotpCommand command,
        EntityId? targetId,
        string action,
        string outcome,
        CancellationToken cancellationToken)
    {
        Result<T> result = await RateLimitedLoginTotpFailureAsync<T>(
            code,
            description,
            command.IpAddress,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsFailure
            || result.Error.Code is IdentityErrorCodes.RateLimitExceeded
                or IdentityErrorCodes.CoordinationUnavailable)
        {
            return result;
        }

        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lease = unitOfWork.ConfigureAwait(false);
        await AppendAuditAsync(
            actor: null,
            action,
            targetId,
            command.RequestId,
            reason: null,
            command.IpAddress,
            command.UserAgent,
            outcome,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<Result<T>> RateLimitedLoginTotpFailureAsync<T>(
        string code,
        string description,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        Result<T>? rateFailure = await RecordFailureAsync<T>(
            ipAddress,
            cancellationToken).ConfigureAwait(false);
        return rateFailure ?? Failure<T>(code, description);
    }

    private TokenPairView IssueTokenPair(
        AuthenticationUserSnapshot user,
        EntityId familyId,
        RefreshCredentialSecret refresh)
    {
        AccessTokenSecret access = _accessTokenIssuer.Issue(new AccessTokenSubject(
            user.Id,
            user.Role,
            user.TokenVersion,
            familyId));
        return new TokenPairView(
            access.Token,
            refresh.Token,
            SessionPolicy.AccessTokenSeconds,
            SessionPolicy.RefreshTokenSeconds);
    }

    private async ValueTask AppendAuditAsync(
        SessionActor? actor,
        string action,
        EntityId? targetId,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        string outcome,
        IUnitOfWorkContext context,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
            new AuditEntry(
                EntityId.New(),
                ActorType(actor),
                actor?.UserId,
                action,
                "user",
                targetId,
                requestId,
                reason,
                ipAddress,
                userAgent,
                BeforeState: null,
                JsonSerializer.SerializeToElement(new { outcome }),
                EmptyMetadata),
            context,
            cancellationToken).ConfigureAwait(false);

    private static AuditActorType ActorType(SessionActor? actor) => actor?.Role switch
    {
        SystemRole.Admin => AuditActorType.Admin,
        SystemRole.Operator => AuditActorType.Operator,
        SystemRole.Auditor => AuditActorType.Auditor,
        SystemRole.User => AuditActorType.User,
        null => AuditActorType.System,
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    private static CredentialHashCandidate[] ToCandidates(
        IReadOnlyList<RefreshCredentialCandidate> candidates) => candidates
        .Select(static candidate => new CredentialHashCandidate(
            candidate.Hash,
            candidate.PepperVersion))
        .ToArray();

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

    private static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null) => Result.Failure<T>(
            code,
            description,
            retryAfterSeconds);
}
#pragma warning restore MA0051
