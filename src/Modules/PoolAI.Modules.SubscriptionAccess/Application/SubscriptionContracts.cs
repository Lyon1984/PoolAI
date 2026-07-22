#pragma warning disable MA0048 // Transport-neutral use-case contracts are intentionally collocated.
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.SubscriptionAccess.Application;

public enum SubscriptionTemplateLifecycle
{
    Active,
    Disabled,
    Retired,
}

public enum SubscriptionLifecycle
{
    Active,
    Suspended,
    Revoked,
}

public enum SubscriptionEffectiveLifecycle
{
    Scheduled,
    Active,
    Expired,
    Suspended,
    Revoked,
}

public sealed record SubscriptionActor(
    EntityId UserId,
    SystemRole Role,
    long TokenVersion);

public sealed record SubscriptionTemplateView(
    EntityId Id,
    EntityId GroupId,
    string Name,
    string? Description,
    int DefaultDurationDays,
    SubscriptionTemplateLifecycle Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SubscriptionView(
    EntityId Id,
    EntityId UserId,
    EntityId GroupId,
    EntityId TemplateId,
    string PlanName,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    SubscriptionLifecycle Status,
    SubscriptionEffectiveLifecycle EffectiveStatus,
    EntityId AssignedBy,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ObservedAt);

public sealed record SubscriptionTemplatePage(
    IReadOnlyList<SubscriptionTemplateView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record SubscriptionPage(
    IReadOnlyList<SubscriptionView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record ListSubscriptionTemplatesQuery(
    SubscriptionActor Actor,
    string? Cursor,
    int Limit = 50);

public sealed record GetSubscriptionTemplateQuery(
    SubscriptionActor Actor,
    EntityId TemplateId);

public sealed record CreateSubscriptionTemplateCommand(
    EntityId RequestId,
    SubscriptionActor Actor,
    string IdempotencyKey,
    EntityId GroupId,
    string Name,
    string? Description,
    int DefaultDurationDays,
    string? IpAddress,
    string? UserAgent);

public sealed record UpdateSubscriptionTemplateCommand(
    EntityId RequestId,
    SubscriptionActor Actor,
    string IdempotencyKey,
    EntityId TemplateId,
    long ExpectedVersion,
    bool NameSpecified,
    string? Name,
    bool DescriptionSpecified,
    string? Description,
    bool DefaultDurationDaysSpecified,
    int? DefaultDurationDays,
    bool StatusSpecified,
    SubscriptionTemplateLifecycle? Status,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record RetireSubscriptionTemplateCommand(
    EntityId RequestId,
    SubscriptionActor Actor,
    string IdempotencyKey,
    EntityId TemplateId,
    long ExpectedVersion,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record ListSubscriptionsQuery(
    SubscriptionActor Actor,
    string? Cursor,
    int Limit,
    EntityId? UserId,
    EntityId? GroupId,
    bool IsSelfQuery);

public sealed record GetSubscriptionQuery(
    SubscriptionActor Actor,
    EntityId SubscriptionId);

public sealed record AssignSubscriptionCommand(
    EntityId RequestId,
    SubscriptionActor Actor,
    string IdempotencyKey,
    EntityId UserId,
    EntityId TemplateId,
    DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record UpdateSubscriptionCommand(
    EntityId RequestId,
    SubscriptionActor Actor,
    string IdempotencyKey,
    EntityId SubscriptionId,
    long ExpectedVersion,
    bool StartsAtSpecified,
    DateTimeOffset? StartsAt,
    bool ExpiresAtSpecified,
    DateTimeOffset? ExpiresAt,
    bool StatusSpecified,
    SubscriptionLifecycle? Status,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record SubscriptionCommandOutcome<T>(
    int StatusCode,
    bool IsReplay,
    T Value,
    string ETag,
    string? Location = null);

public sealed record SubscriptionCommandOutcome(
    int StatusCode,
    bool IsReplay,
    string ETag);

public static class SubscriptionErrorCodes
{
    public const string CoordinationUnavailable = "coordination_unavailable";
    public const string GroupDisabled = "group_disabled";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidRequest = "invalid_request";
    public const string ResourceConflict = "resource_conflict";
    public const string ResourceNotFound = "resource_not_found";
    public const string RoleRequired = "role_required";
    public const string SubscriptionConflict = "subscription_conflict";
    public const string SubscriptionTemplateDisabled = "subscription_template_disabled";
    public const string ValidationFailed = "validation_failed";
    public const string VersionConflict = "version_conflict";
}
#pragma warning restore MA0048
