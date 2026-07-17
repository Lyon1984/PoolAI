#pragma warning disable MA0048 // Transport-neutral Group contracts are intentionally collocated.
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Application;

public enum GroupControlRole
{
    Admin,
    Operator,
    Auditor,
    User,
}

public sealed record GroupActor(
    EntityId UserId,
    GroupControlRole Role,
    long TokenVersion);

public sealed record ListGroupsQuery(
    GroupActor Actor,
    string? Cursor,
    int Limit = 50);

public sealed record GetGroupQuery(
    GroupActor Actor,
    EntityId GroupId);

public sealed record CreateGroupCommand(
    EntityId RequestId,
    GroupActor Actor,
    string IdempotencyKey,
    string Name,
    string? Description,
    long TotalTokens,
    string? IpAddress,
    string? UserAgent);

public sealed record UpdateGroupCommand(
    EntityId RequestId,
    GroupActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    long ExpectedVersion,
    bool HasName,
    string? Name,
    bool HasDescription,
    string? Description,
    bool HasStatus,
    GroupLifecycle? Status,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record GroupView(
    EntityId Id,
    string Name,
    string? Description,
    GroupLifecycle Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record GroupPage(
    IReadOnlyList<GroupView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record GroupCommandOutcome(
    int StatusCode,
    bool IsReplay,
    GroupView Value,
    string ETag,
    string? Location = null);

public static class GroupErrorCodes
{
    public const string CoordinationUnavailable = "coordination_unavailable";
    public const string GroupActivationNotReady = "group_activation_not_ready";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string ResourceConflict = "resource_conflict";
    public const string ResourceNotFound = "resource_not_found";
    public const string RoleRequired = "role_required";
    public const string ValidationFailed = "validation_failed";
    public const string VersionConflict = "version_conflict";
}
#pragma warning restore MA0048
