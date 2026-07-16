#pragma warning disable MA0048 // Public transport-neutral contracts are intentionally collocated.
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Application;

public sealed record IdentityActor(
    EntityId UserId,
    SystemRole Role,
    long TokenVersion);

public sealed record ListUsersQuery(
    IdentityActor Actor,
    string? Cursor,
    int Limit = 50);

public sealed record GetUserQuery(
    IdentityActor Actor,
    EntityId UserId);

public sealed record CreateUserCommand(
    EntityId RequestId,
    IdentityActor Actor,
    string IdempotencyKey,
    string Email,
    string DisplayName,
    SystemRole Role,
    string TemporaryPassword,
    string? IpAddress,
    string? UserAgent);

public sealed record UpdateUserCommand(
    EntityId RequestId,
    IdentityActor Actor,
    string IdempotencyKey,
    EntityId UserId,
    long ExpectedVersion,
    string? DisplayName,
    SystemRole? Role,
    UserLifecycle? Status,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record AdminPasswordResetCommand(
    EntityId RequestId,
    IdentityActor Actor,
    string IdempotencyKey,
    EntityId UserId,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record ForgotPasswordCommand(
    EntityId RequestId,
    string Email,
    string? IpAddress,
    string? UserAgent);

public sealed record CompletePasswordResetCommand(
    EntityId RequestId,
    string Token,
    string NewPassword,
    string? IpAddress,
    string? UserAgent);

public sealed record UserView(
    EntityId Id,
    string Email,
    string DisplayName,
    SystemRole Role,
    UserLifecycle Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserPage(
    IReadOnlyList<UserView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record IdentityCommandOutcome<T>(
    int StatusCode,
    bool IsReplay,
    T Value,
    string? ETag = null,
    string? Location = null);

public sealed record IdentityCommandOutcome(
    int StatusCode,
    bool IsReplay);

public static class IdentityErrorCodes
{
    public const string CoordinationUnavailable = "coordination_unavailable";
    public const string DependencyUnavailable = "dependency_unavailable";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string PasswordPolicyFailed = "password_policy_failed";
    public const string PasswordResetTokenInvalid = "password_reset_token_invalid";
    public const string RateLimitExceeded = "rate_limit_exceeded";
    public const string ResourceConflict = "resource_conflict";
    public const string ResourceNotFound = "resource_not_found";
    public const string RoleRequired = "role_required";
    public const string ValidationFailed = "validation_failed";
    public const string VersionConflict = "version_conflict";
}
#pragma warning restore MA0048
