#pragma warning disable MA0048 // The session transport-neutral contracts are one cohesive public surface.
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Application;

public sealed record SessionActor(
    EntityId UserId,
    SystemRole Role,
    long TokenVersion,
    EntityId SessionFamilyId);

public sealed record LoginCommand(
    EntityId RequestId,
    string Email,
    string Password,
    string? IpAddress,
    string? UserAgent);

public sealed record VerifyLoginTotpCommand(
    EntityId RequestId,
    EntityId ChallengeId,
    string TotpCode,
    string? IpAddress,
    string? UserAgent);

public sealed record RefreshSessionCommand(
    EntityId RequestId,
    string RefreshToken,
    string? IpAddress,
    string? UserAgent);

public sealed record LogoutCommand(
    EntityId RequestId,
    SessionActor Actor,
    string? RefreshToken,
    bool AllSessions,
    string? IpAddress,
    string? UserAgent);

public sealed record GetCurrentUserQuery(SessionActor Actor);

public sealed record ChangePasswordCommand(
    EntityId RequestId,
    SessionActor Actor,
    string IdempotencyKey,
    long ExpectedVersion,
    string CurrentPassword,
    string NewPassword,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record SetupTotpCommand(
    EntityId RequestId,
    SessionActor Actor,
    string IdempotencyKey,
    string CurrentPassword,
    string? IpAddress,
    string? UserAgent);

public sealed record ConfirmTotpCommand(
    EntityId RequestId,
    SessionActor Actor,
    string IdempotencyKey,
    long ExpectedVersion,
    EntityId ChallengeId,
    string TotpCode,
    string? IpAddress,
    string? UserAgent);

public sealed record DisableTotpCommand(
    EntityId RequestId,
    SessionActor Actor,
    string IdempotencyKey,
    long ExpectedVersion,
    string CurrentPassword,
    string TotpCode,
    string? IpAddress,
    string? UserAgent);

public sealed record TokenPairView(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    int RefreshExpiresIn);

public abstract record LoginResultView;

public sealed record LoginTokenResultView(TokenPairView Tokens) : LoginResultView;

public sealed record LoginMfaResultView(
    EntityId ChallengeId,
    int ExpiresIn) : LoginResultView;

public sealed record TotpSetupView(
    EntityId ChallengeId,
    string Secret,
    string OtpauthUri,
    int ExpiresIn);

public sealed record TotpConfirmView(IReadOnlyList<string> RecoveryCodes);

public sealed record CurrentUserView(
    EntityId Id,
    string Email,
    string DisplayName,
    SystemRole Role,
    UserLifecycle Status,
    bool TotpEnabled,
    DateTimeOffset? PasswordChangedAt,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
#pragma warning restore MA0048
