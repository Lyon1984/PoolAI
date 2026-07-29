#pragma warning disable MA0048 // Transport-neutral Account contracts are intentionally collocated.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Application;

public enum AccountControlRole
{
    Admin,
    Operator,
    Auditor,
    User,
}

public enum AccountLifecycle
{
    Active,
    Disabled,
    Retired,
}

public sealed record AccountActor(
    EntityId UserId,
    AccountControlRole Role,
    long TokenVersion);

public sealed record AccountHealthView(
    AccountHealth Status,
    DateTimeOffset? RetryAt,
    DateTimeOffset? LastCheckedAt);

public sealed record AccountView(
    EntityId Id,
    string Name,
    UpstreamProvider Provider,
    Uri BaseUrl,
    string CredentialPrefix,
    AccountLifecycle Status,
    AccountHealthView Health,
    int ActiveLeases,
    int MaxConcurrency,
    int Priority,
    int Weight,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AccountPage(
    IReadOnlyList<AccountView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record ListAccountsQuery(
    AccountActor Actor,
    string? Cursor,
    int Limit = 50);

public sealed record GetAccountQuery(
    AccountActor Actor,
    EntityId AccountId);

public sealed record CreateAccountCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    string Name,
    UpstreamProvider Provider,
    string BaseUrl,
    string Credential,
    int MaxConcurrency,
    int Priority,
    int Weight,
    string? IpAddress,
    string? UserAgent)
{
    public override string ToString() => nameof(CreateAccountCommand);
}

public sealed record UpdateAccountCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId AccountId,
    long ExpectedVersion,
    bool NameSpecified,
    string? Name,
    bool BaseUrlSpecified,
    string? BaseUrl,
    bool CredentialSpecified,
    string? Credential,
    bool StatusSpecified,
    AccountLifecycle? Status,
    bool MaxConcurrencySpecified,
    int? MaxConcurrency,
    bool PrioritySpecified,
    int? Priority,
    bool WeightSpecified,
    int? Weight,
    string? Reason,
    string? IpAddress,
    string? UserAgent)
{
    public override string ToString() => nameof(UpdateAccountCommand);
}

public sealed record RetireAccountCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId AccountId,
    long ExpectedVersion,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record AccountCommandOutcome<T>(
    int StatusCode,
    bool IsReplay,
    T Value,
    string ETag,
    string? Location = null);

public sealed record AccountCommandOutcome(
    int StatusCode,
    bool IsReplay,
    string ETag);

public static class AccountErrorCodes
{
    public const string AccountInUse = "account_in_use";
    public const string CoordinationUnavailable = "coordination_unavailable";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string ResourceConflict = "resource_conflict";
    public const string ResourceNotFound = "resource_not_found";
    public const string RoleRequired = "role_required";
    public const string ValidationFailed = "validation_failed";
    public const string VersionConflict = "version_conflict";
}
#pragma warning restore MA0048
