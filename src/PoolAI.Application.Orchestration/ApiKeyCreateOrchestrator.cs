using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.SubscriptionAccess.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed class ApiKeyCreateOrchestrator(
    IApiKeyCreateIdempotencyPreflight idempotencyPreflight,
    ISubscriptionAccessReader subscriptionAccessReader,
    IApiKeyIssuer ownerCommand) : IApiKeyCreateUseCase
{
    public async ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<ApiKeyCreatedOutcome?> preflight = await idempotencyPreflight
            .TryReplayAsync(command, cancellationToken)
            .ConfigureAwait(false);
        if (preflight.IsFailure)
        {
            return ForwardFailure<ApiKeyCreatedOutcome>(preflight.Error);
        }

        if (preflight.Value is ApiKeyCreatedOutcome replay)
        {
            return Result.Success(replay);
        }

        Result<ApiKeyAccessDecision> decision = await ReadAccessDecisionAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        if (decision.IsFailure)
        {
            return ForwardFailure<ApiKeyCreatedOutcome>(decision.Error);
        }

        return await ownerCommand
            .CreateAsync(command, decision.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<Result<ApiKeyAccessDecision>> ReadAccessDecisionAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionAccessSnapshot> access = await subscriptionAccessReader
            .GetEffectiveAccessAsync(
                command.UserId,
                command.GroupId,
                cancellationToken)
            .ConfigureAwait(false);
        if (access.IsSuccess)
        {
            SubscriptionAccessSnapshot snapshot = access.Value;
            if (snapshot.SubscriptionId.Value == Guid.Empty
                || snapshot.UserId != command.UserId
                || snapshot.GroupId != command.GroupId
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
                command.UserId,
                command.GroupId,
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
        if (denial is null)
        {
            return ForwardFailure<ApiKeyAccessDecision>(access.Error);
        }

        return Result.Success(new ApiKeyAccessDecision(
            denial.Value,
            command.UserId,
            command.GroupId,
            SubscriptionId: null,
            ObservedAt: null));
    }

    private static Result<T> ForwardFailure<T>(ResultError error) => Result.Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);
}
