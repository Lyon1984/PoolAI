#pragma warning disable MA0048 // Transport-neutral Channel contracts are intentionally collocated.
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Application;

public enum ChannelLifecycle
{
    Active,
    Disabled,
    Retired,
}

public sealed record ChannelModelMappingView(
    string ClientModel,
    string UpstreamModel);

public sealed record ChannelView(
    EntityId Id,
    string Name,
    UpstreamProvider Provider,
    ChannelLifecycle Status,
    ChannelCapabilitiesSnapshot Capabilities,
    IReadOnlyList<ChannelModelMappingView> ModelMappings,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChannelPage(
    IReadOnlyList<ChannelView> Data,
    string? NextCursor,
    bool HasMore);

public sealed record ListChannelsQuery(
    AccountActor Actor,
    string? Cursor,
    int Limit = 50);

public sealed record GetChannelQuery(
    AccountActor Actor,
    EntityId ChannelId);

public sealed record CreateChannelCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    string Name,
    UpstreamProvider Provider,
    ChannelCapabilitiesSnapshot Capabilities,
    IReadOnlyList<ChannelModelMappingView> ModelMappings,
    string? IpAddress,
    string? UserAgent);

public sealed record UpdateChannelCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId ChannelId,
    long ExpectedVersion,
    bool NameSpecified,
    string? Name,
    bool StatusSpecified,
    ChannelLifecycle? Status,
    bool CapabilitiesSpecified,
    ChannelCapabilitiesSnapshot? Capabilities,
    bool ModelMappingsSpecified,
    IReadOnlyList<ChannelModelMappingView>? ModelMappings,
    string? Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record RetireChannelCommand(
    EntityId RequestId,
    AccountActor Actor,
    string IdempotencyKey,
    EntityId ChannelId,
    long ExpectedVersion,
    string Reason,
    string? IpAddress,
    string? UserAgent);

public sealed record SupplyCommandOutcome<T>(
    int StatusCode,
    bool IsReplay,
    T Value,
    string ETag,
    string? Location = null);

public sealed record SupplyCommandOutcome(
    int StatusCode,
    bool IsReplay,
    string ETag);

public static class SupplyControlErrorCodes
{
    public const string ChannelInUse = "channel_in_use";
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
