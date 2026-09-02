using System.Net;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

/// <summary>
/// Composes the canonical PostgreSQL authorization reads for one inbound model
/// request. It does not read Redis, route an Account, or reserve quota.
/// </summary>
internal sealed class GatewayCanonicalAdmissionService(
    IApiKeyAuthenticator apiKeys,
    IUserStatusReader users,
    ISubscriptionAccessReader subscriptions,
    IGroupStatusReader groups,
    GatewayClientIpResolver clientIpResolver)
{
    private readonly IApiKeyAuthenticator _apiKeys = apiKeys
        ?? throw new ArgumentNullException(nameof(apiKeys));
    private readonly IUserStatusReader _users = users
        ?? throw new ArgumentNullException(nameof(users));
    private readonly ISubscriptionAccessReader _subscriptions = subscriptions
        ?? throw new ArgumentNullException(nameof(subscriptions));
    private readonly IGroupStatusReader _groups = groups
        ?? throw new ArgumentNullException(nameof(groups));
    private readonly GatewayClientIpResolver _clientIpResolver = clientIpResolver
        ?? throw new ArgumentNullException(nameof(clientIpResolver));

    internal async ValueTask<Result<GatewayCanonicalAccess>> AuthorizeAsync(
        string presentedApiKey,
        IPAddress? socketPeer,
        IReadOnlyList<string>? forwardedForFieldValues,
        CancellationToken cancellationToken)
    {
        Result<ApiKeyAccessSnapshot> apiKey = await AuthenticateAsync(
                presentedApiKey,
                socketPeer,
                forwardedForFieldValues,
                cancellationToken)
            .ConfigureAwait(false);
        if (apiKey.IsFailure)
        {
            return CopyFailure<GatewayCanonicalAccess>(apiKey.Error);
        }

        Result<UserStatusSnapshot> user = await ReadUserAsync(
                apiKey.Value.UserId,
                cancellationToken)
            .ConfigureAwait(false);
        if (user.IsFailure)
        {
            return CopyFailure<GatewayCanonicalAccess>(user.Error);
        }

        Result<SubscriptionAccessSnapshot> subscription =
            await ReadSubscriptionAsync(
                    user.Value.UserId,
                    apiKey.Value.GroupId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (subscription.IsFailure)
        {
            return CopyFailure<GatewayCanonicalAccess>(subscription.Error);
        }

        Result<GroupSnapshot> group = await ReadGroupAsync(
                apiKey.Value.GroupId,
                cancellationToken)
            .ConfigureAwait(false);
        if (group.IsFailure)
        {
            return CopyFailure<GatewayCanonicalAccess>(group.Error);
        }

        return Result.Success(new GatewayCanonicalAccess(
            apiKey.Value,
            user.Value,
            subscription.Value,
            group.Value));
    }

    private async ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
        string presentedApiKey,
        IPAddress? socketPeer,
        IReadOnlyList<string>? forwardedForFieldValues,
        CancellationToken cancellationToken)
    {
        Result<ApiKeyAccessSnapshot> apiKey = await _apiKeys
            .AuthenticateAsync(presentedApiKey, cancellationToken)
            .ConfigureAwait(false);
        if (apiKey.IsFailure)
        {
            return apiKey;
        }

        return IsValidApiKeySnapshot(apiKey.Value)
            && _clientIpResolver.TryResolveAuthorizedClientAddress(
                socketPeer,
                forwardedForFieldValues,
                apiKey.Value.AllowedCidrs,
                out _)
            ? apiKey
            : Result.Failure<ApiKeyAccessSnapshot>(
                ErrorCodesV1.InvalidApiKey,
                "The API Key is invalid.");
    }

    private async ValueTask<Result<UserStatusSnapshot>> ReadUserAsync(
        EntityId userId,
        CancellationToken cancellationToken)
    {
        Result<UserStatusSnapshot> user = await _users
            .GetCurrentAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (user.IsFailure)
        {
            return string.Equals(
                user.Error.Code,
                "resource_not_found",
                StringComparison.Ordinal)
                ? Result.Failure<UserStatusSnapshot>(
                    ErrorCodesV1.InvalidApiKey,
                    "The API Key is invalid.")
                : user;
        }

        if (!IsValidUserSnapshot(user.Value, userId))
        {
            return DependencyUnavailable<UserStatusSnapshot>();
        }

        return user.Value.Lifecycle == UserLifecycle.Active
            ? user
            : Result.Failure<UserStatusSnapshot>(
                ErrorCodesV1.UserDisabled,
                "The API Key owner is disabled.");
    }

    private async ValueTask<Result<SubscriptionAccessSnapshot>>
        ReadSubscriptionAsync(
            EntityId userId,
            EntityId groupId,
            CancellationToken cancellationToken)
    {
        Result<SubscriptionAccessSnapshot> subscription =
            await _subscriptions.GetEffectiveAccessAsync(
                    userId,
                    groupId,
                    cancellationToken)
                .ConfigureAwait(false);
        return subscription.IsFailure
            || IsValidSubscriptionSnapshot(subscription.Value, userId, groupId)
            ? subscription
            : DependencyUnavailable<SubscriptionAccessSnapshot>();
    }

    private async ValueTask<Result<GroupSnapshot>> ReadGroupAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        Result<GroupSnapshot> group = await _groups
            .GetAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (group.IsFailure)
        {
            return string.Equals(
                group.Error.Code,
                "resource_not_found",
                StringComparison.Ordinal)
                ? DependencyUnavailable<GroupSnapshot>()
                : group;
        }

        if (!IsValidGroupSnapshot(group.Value, groupId))
        {
            return DependencyUnavailable<GroupSnapshot>();
        }

        if (group.Value.Lifecycle != GroupLifecycle.Active)
        {
            return Result.Failure<GroupSnapshot>(
                ErrorCodesV1.GroupDisabled,
                "The canonical Group is disabled.");
        }

        return group.Value.HasCurrentQuotaPeriod
            ? group
            : Result.Failure<GroupSnapshot>(
                "group_activation_not_ready",
                "The active Group has no current quota period.");
    }

    private static bool IsValidApiKeySnapshot(ApiKeyAccessSnapshot value) =>
        value.ApiKeyId.Value.Version == 7
        && value.UserId.Value.Version == 7
        && value.GroupId.Value.Version == 7
        && value.IsEffective
        && value.AllowedCidrs is not null
        && value.Version > 0
        && value.ObservedAt != default;

    private static bool IsValidUserSnapshot(
        UserStatusSnapshot value,
        EntityId expectedUserId) =>
        value.UserId == expectedUserId
        && Enum.IsDefined(value.Lifecycle)
        && Enum.IsDefined(value.Role)
        && value.TokenVersion > 0
        && value.Version > 0
        && value.ObservedAt != default;

    private static bool IsValidSubscriptionSnapshot(
        SubscriptionAccessSnapshot value,
        EntityId expectedUserId,
        EntityId expectedGroupId) =>
        value.SubscriptionId.Value.Version == 7
        && value.UserId == expectedUserId
        && value.GroupId == expectedGroupId
        && !string.IsNullOrWhiteSpace(value.PlanName)
        && value.StartsAt != default
        && value.ExpiresAt > value.StartsAt
        && value.EffectiveStatus == SubscriptionEffectiveStatus.Active
        && value.Version > 0
        && value.ObservedAt != default;

    private static bool IsValidGroupSnapshot(
        GroupSnapshot value,
        EntityId expectedGroupId) =>
        value.GroupId == expectedGroupId
        && Enum.IsDefined(value.Lifecycle)
        && value.Version > 0
        && value.ObservedAt != default
        && value.RequestsPerMinute is >= 1 and <= 1_000_000;

    private static Result<T> DependencyUnavailable<T>() =>
        Result.Failure<T>(
            ErrorCodesV1.DependencyUnavailable,
            "Canonical Gateway admission state is inconsistent.",
            retryAfterSeconds: 1);

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);
}
