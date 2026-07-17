#pragma warning disable MA0048 // Session persistence requests and results form one internal port.
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Modules.Identity.Application.Ports;

internal sealed record AuthenticationUserSnapshot(
    EntityId Id,
    string Email,
    string NormalizedEmail,
    string DisplayName,
    string PasswordHash,
    SystemRole Role,
    UserLifecycle Status,
    JsonElement? TotpSecretEnvelope,
    long? TotpLastAcceptedStep,
    EntityId SecurityStamp,
    long TokenVersion,
    int FailedLoginCount,
    DateTimeOffset? LockedUntil,
    DateTimeOffset? LastLoginAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    internal bool TotpEnabled => TotpSecretEnvelope is not null;

    internal CurrentUserView ToCurrentUserView() => new(
        Id,
        Email,
        DisplayName,
        Role,
        Status,
        TotpEnabled,
        PasswordChangedAt: null,
        Version,
        CreatedAt,
        UpdatedAt);
}

internal sealed record CredentialHashCandidate(byte[] Hash, short PepperVersion);

internal sealed record RefreshSessionWrite(
    EntityId Id,
    byte[] TokenHash,
    short PepperVersion,
    TimeSpan Lifetime,
    string? IpAddress,
    string? UserAgent);

internal sealed record TotpChallengeWrite(
    EntityId Id,
    string Kind,
    byte[] TokenHash,
    short PepperVersion,
    TimeSpan Lifetime,
    JsonElement? SecretEnvelope,
    JsonElement? ResponseBodyEnvelope,
    EntityId SecurityStamp,
    long TokenVersion);

internal sealed record PasswordLoginWrite(
    EntityId UserId,
    EntityId ExpectedSecurityStamp,
    RefreshSessionWrite? Session,
    TotpChallengeWrite? Challenge);

internal enum PasswordFailureDisposition
{
    Recorded,
    Ignored,
}

internal enum PasswordLoginDisposition
{
    SessionCreated,
    MfaRequired,
    AccountLocked,
    UserDisabled,
    StaleCredential,
}

internal sealed record PasswordLoginPersistenceResult(
    PasswordLoginDisposition Disposition,
    AuthenticationUserSnapshot? User,
    EntityId? SessionFamilyId,
    long? RetryAfterSeconds);

internal sealed record TotpChallengeSnapshot(
    EntityId Id,
    EntityId UserId,
    string Kind,
    JsonElement? SecretEnvelope,
    JsonElement? ResponseBodyEnvelope,
    EntityId SecurityStamp,
    long TokenVersion,
    DateTimeOffset ExpiresAt,
    AuthenticationUserSnapshot User);

internal enum MfaLoginDisposition
{
    SessionCreated,
    ChallengeInvalid,
    TotpReplay,
}

internal sealed record MfaLoginPersistenceResult(
    MfaLoginDisposition Disposition,
    AuthenticationUserSnapshot? User,
    EntityId? SessionFamilyId);

internal enum RefreshRotationDisposition
{
    Rotated,
    Invalid,
    Reused,
}

internal sealed record RefreshRotationPersistenceResult(
    RefreshRotationDisposition Disposition,
    AuthenticationUserSnapshot? User,
    EntityId? SessionFamilyId);

internal sealed record LogoutPersistenceResult(
    AuthenticationUserSnapshot? User,
    bool Changed);

internal enum SecurityMutationDisposition
{
    Updated,
    NotFound,
    VersionConflict,
    InvalidCredential,
    TotpAlreadyEnabled,
    TotpNotEnabled,
    ChallengeInvalid,
    ChallengeExpired,
    TotpReplay,
}

internal sealed record SecurityMutationPersistenceResult(
    SecurityMutationDisposition Disposition,
    AuthenticationUserSnapshot? User,
    long? CurrentVersion = null);

internal sealed record TotpRecoveryCodeWrite(
    EntityId Id,
    byte[] CodeHash,
    short PepperVersion);

internal sealed record TotpConfirmWrite(
    EntityId UserId,
    long ExpectedVersion,
    IReadOnlyList<CredentialHashCandidate> ChallengeCandidates,
    long AcceptedStep,
    JsonElement UserSecretEnvelope,
    JsonElement RecoveryCodesEnvelope,
    IReadOnlyList<TotpRecoveryCodeWrite> RecoveryCodes);

internal interface IIdentitySessionRepository
{
    ValueTask<AuthenticationUserSnapshot?> FindAuthenticationUserAsync(
        string normalizedEmail,
        CancellationToken cancellationToken);

    ValueTask<AuthenticationUserSnapshot?> GetAuthenticationUserAsync(
        EntityId userId,
        CancellationToken cancellationToken);

    ValueTask<UserStatusSnapshot?> ReadCanonicalAuthorizationAsync(
        EntityId userId,
        EntityId familyId,
        long tokenVersion,
        CancellationToken cancellationToken);

    ValueTask<bool> HasRefreshCredentialAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        CancellationToken cancellationToken);

    ValueTask<PasswordFailureDisposition> RecordPasswordFailureAsync(
        EntityId userId,
        EntityId expectedSecurityStamp,
        int maximumFailures,
        TimeSpan lockoutDuration,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<PasswordLoginPersistenceResult> CompletePasswordLoginAsync(
        PasswordLoginWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<TotpChallengeSnapshot?> FindTotpChallengeAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        string kind,
        CancellationToken cancellationToken);

    ValueTask<MfaLoginPersistenceResult> CompleteMfaLoginAsync(
        IReadOnlyList<CredentialHashCandidate> challengeCandidates,
        long acceptedStep,
        RefreshSessionWrite session,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<RefreshRotationPersistenceResult> RotateRefreshSessionAsync(
        IReadOnlyList<CredentialHashCandidate> candidates,
        RefreshSessionWrite replacement,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<LogoutPersistenceResult> LogoutAsync(
        SessionActor actor,
        IReadOnlyList<CredentialHashCandidate> refreshCandidates,
        bool allSessions,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SecurityMutationPersistenceResult> ChangePasswordAsync(
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        string passwordHash,
        EntityId newSecurityStamp,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SecurityMutationPersistenceResult> CreateTotpSetupAsync(
        EntityId userId,
        EntityId expectedSecurityStamp,
        TotpChallengeWrite challenge,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SecurityMutationPersistenceResult> ConfirmTotpAsync(
        TotpConfirmWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<SecurityMutationPersistenceResult> DisableTotpAsync(
        EntityId userId,
        long expectedVersion,
        EntityId expectedSecurityStamp,
        long acceptedStep,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}
#pragma warning restore MA0048
