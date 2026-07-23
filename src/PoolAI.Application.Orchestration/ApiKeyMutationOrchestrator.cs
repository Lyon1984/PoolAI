using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed class ApiKeyMutationOrchestrator(
    IApiKeyMutationIdempotencyPreflight idempotencyPreflight,
    IApiKeyControlPlaneReader reader,
    ISubscriptionAccessReader subscriptionAccessReader,
    IApiKeyMutationOwner ownerCommand) : IApiKeyMutationUseCase
{
    public async ValueTask<Result<ApiKeyUpdatedOutcome>> UpdateAsync(
        UpdateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Result<ApiKeyUpdatedOutcome?> preflight = await idempotencyPreflight
            .TryReplayUpdateAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (preflight.IsFailure)
        {
            return ForwardFailure<ApiKeyUpdatedOutcome>(preflight.Error);
        }

        if (preflight.Value is ApiKeyUpdatedOutcome replay)
        {
            return Result.Success(replay);
        }

        Result<ApiKeyControlPlaneSnapshot> current = await ReadAsync(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return ForwardFailure<ApiKeyUpdatedOutcome>(current.Error);
        }

        ApiKeyControlPlaneSnapshot snapshot = current.Value;
        ApiKeyAccessDecision? decision = null;
        if (snapshot.Status != ApiKeyPersistentStatus.Revoked
            && RequiresSubscriptionGate(command, snapshot))
        {
            Result<ApiKeyAccessDecision> access = await ReadAccessDecisionAsync(
                command.UserId,
                snapshot.GroupId,
                cancellationToken).ConfigureAwait(false);
            if (access.IsFailure)
            {
                return ForwardFailure<ApiKeyUpdatedOutcome>(access.Error);
            }

            decision = access.Value;
        }

        return await ownerCommand
            .UpdateAsync(command, snapshot, decision, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Result<ApiKeyRevokedOutcome>> RevokeAsync(
        RevokeApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Result<ApiKeyRevokedOutcome?> preflight = await idempotencyPreflight
            .TryReplayRevokeAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (preflight.IsFailure)
        {
            return ForwardFailure<ApiKeyRevokedOutcome>(preflight.Error);
        }

        if (preflight.Value is ApiKeyRevokedOutcome replay)
        {
            return Result.Success(replay);
        }

        Result<ApiKeyControlPlaneSnapshot> current = await ReadAsync(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return ForwardFailure<ApiKeyRevokedOutcome>(current.Error);
        }

        return await ownerCommand
            .RevokeAsync(command, current.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<Result<ApiKeyCreatedOutcome>> RotateAsync(
        RotateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Result<ApiKeyCreatedOutcome?> preflight = await idempotencyPreflight
            .TryReplayRotateAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (preflight.IsFailure)
        {
            return ForwardFailure<ApiKeyCreatedOutcome>(preflight.Error);
        }

        if (preflight.Value is ApiKeyCreatedOutcome replay)
        {
            return Result.Success(replay);
        }

        Result<ApiKeyControlPlaneSnapshot> current = await ReadAsync(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return ForwardFailure<ApiKeyCreatedOutcome>(current.Error);
        }

        ApiKeyControlPlaneSnapshot snapshot = current.Value;
        ApiKeyAccessDecision? decision = null;
        if (snapshot.Status != ApiKeyPersistentStatus.Revoked)
        {
            Result<ApiKeyAccessDecision> access = await ReadAccessDecisionAsync(
                command.UserId,
                snapshot.GroupId,
                cancellationToken).ConfigureAwait(false);
            if (access.IsFailure)
            {
                return ForwardFailure<ApiKeyCreatedOutcome>(access.Error);
            }

            decision = access.Value;
        }

        return await ownerCommand
            .RotateAsync(command, snapshot, decision, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<Result<ApiKeyControlPlaneSnapshot>> ReadAsync(
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId,
        CancellationToken cancellationToken) => reader.GetAsync(
        new GetApiKeyQuery(actor, accessMode, userId, apiKeyId),
        cancellationToken);

    private async ValueTask<Result<ApiKeyAccessDecision>> ReadAccessDecisionAsync(
        EntityId userId,
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionAccessSnapshot> access = await subscriptionAccessReader
            .GetEffectiveAccessAsync(userId, groupId, cancellationToken)
            .ConfigureAwait(false);
        if (access.IsSuccess)
        {
            SubscriptionAccessSnapshot snapshot = access.Value;
            if (snapshot.SubscriptionId.Value == Guid.Empty
                || snapshot.UserId != userId
                || snapshot.GroupId != groupId
                || snapshot.EffectiveStatus != SubscriptionEffectiveStatus.Active
                || snapshot.ObservedAt == default)
            {
                return Result.Failure<ApiKeyAccessDecision>(
                    "dependency_unavailable",
                    "The Subscription access authority returned inconsistent evidence.",
                    retryAfterSeconds: 1);
            }

            return Result.Success(new ApiKeyAccessDecision(
                ApiKeyAccessDecisionKind.Authorized,
                userId,
                groupId,
                snapshot.SubscriptionId,
                snapshot.ObservedAt));
        }

        ApiKeyAccessDecisionKind? denial = access.Error.Code switch
        {
            "subscription_required" =>
                ApiKeyAccessDecisionKind.SubscriptionRequired,
            "subscription_inactive" =>
                ApiKeyAccessDecisionKind.SubscriptionInactive,
            _ => null,
        };
        return denial is ApiKeyAccessDecisionKind kind
            ? Result.Success(new ApiKeyAccessDecision(
                kind,
                userId,
                groupId,
                SubscriptionId: null,
                ObservedAt: null))
            : ForwardFailure<ApiKeyAccessDecision>(access.Error);
    }

    private static bool RequiresSubscriptionGate(
        UpdateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot)
    {
        ApiKeyPersistentStatus finalStatus = command.SetStatus
            ? command.Status ?? snapshot.Status
            : snapshot.Status;
        DateTimeOffset? finalExpiry = command.SetExpiresAt
            ? command.ExpiresAt?.ToUniversalTime()
            : snapshot.ExpiresAt;
        bool finalEffectiveActive = finalStatus == ApiKeyPersistentStatus.Active
            && (finalExpiry is null || finalExpiry > snapshot.ObservedAt);
        bool enablesDisabled = snapshot.Status == ApiKeyPersistentStatus.Disabled
            && finalStatus == ApiKeyPersistentStatus.Active;
        bool restoresExpired = snapshot.EffectiveStatus == ApiKeyEffectiveStatus.Expired
            && finalEffectiveActive;
        return enablesDisabled || restoresExpired;
    }

    private static Result<T> ForwardFailure<T>(ResultError error) => Result.Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);
}
