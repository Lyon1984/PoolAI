using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed class GroupActivationOrchestrator(
    IUserStatusReader userStatusReader,
    IGroupSupplyReadiness supplyReadiness,
    IGroupActivationCommand activationCommand,
    IGroupActivationIdempotencyPreflight idempotencyPreflight) : IGroupActivationOrchestrator
{
    public ValueTask<Result<GroupActivationResult>> ActivateAsync(
        GroupActivationOrchestrationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ActivateAsync(
            new GroupActivationRequest(
                command.Actor,
                command.GroupId,
                command.ExpectedGroupVersion,
                command.IdempotencyKey,
                command.Reason,
                command.MetadataPatch,
                command.RequestId,
                command.IpAddress,
                command.UserAgent),
            cancellationToken);
    }

    public async ValueTask<Result<GroupActivationResult>> ActivateAsync(
        GroupActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<Unit> requestValidation = ValidateRequest(request);
        if (requestValidation.IsFailure)
        {
            return ForwardFailure<GroupActivationResult>(requestValidation.Error);
        }

        Result<Unit> authorization = await AuthorizeActorAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.IsFailure)
        {
            return ForwardFailure<GroupActivationResult>(authorization.Error);
        }

        Result<GroupActivationResult?> preflight = await TryReplayAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (preflight.IsFailure)
        {
            return ForwardFailure<GroupActivationResult>(preflight.Error);
        }

        if (preflight.Value is GroupActivationResult replay)
        {
            return Result.Success(replay);
        }

        Result<SupplyReadinessEvidence?> evidence = await ObserveSupplyAsync(
            request.GroupId,
            cancellationToken).ConfigureAwait(false);
        if (evidence.IsFailure)
        {
            return ForwardFailure<GroupActivationResult>(evidence.Error);
        }

        return await activationCommand
            .ActivateAsync(CreateActivationCommand(request, evidence.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    private static ActivateGroupCommand CreateActivationCommand(
        GroupActivationRequest request,
        SupplyReadinessEvidence? evidence) => new(
            request.Actor,
            request.GroupId,
            request.ExpectedGroupVersion,
            request.IdempotencyKey,
            request.Reason,
            evidence,
            request.MetadataPatch,
            request.RequestId,
            request.IpAddress,
            request.UserAgent);

    private ValueTask<Result<GroupActivationResult?>> TryReplayAsync(
        GroupActivationRequest request,
        CancellationToken cancellationToken) => idempotencyPreflight.TryReplayAsync(
            new GroupActivationOrchestrationCommand(
                request.Actor,
                request.GroupId,
                request.ExpectedGroupVersion,
                request.IdempotencyKey,
                request.Reason,
                request.MetadataPatch,
                request.RequestId,
                request.IpAddress,
                request.UserAgent),
            cancellationToken);

    private static Result<Unit> ValidateRequest(GroupActivationRequest request)
    {
        if (request.ExpectedGroupVersion < 1)
        {
            return Result.Failure<Unit>(
                "validation_failed",
                "The expected Group version must be positive.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Result.Failure<Unit>(
                "idempotency_key_required",
                "Group activation requires an idempotency key.");
        }

        if (request.MetadataPatch is GroupMetadataPatch metadata
            && (metadata.HasName
                && (string.IsNullOrWhiteSpace(metadata.Name)
                    || metadata.Name.Length > 100)
                || metadata.HasDescription
                && metadata.Description is { Length: > 1000 }))
        {
            return Result.Failure<Unit>(
                "validation_failed",
                "The Group metadata patch is invalid.");
        }

        return string.IsNullOrWhiteSpace(request.Reason)
            ? Result.Failure<Unit>(
                "validation_failed",
                "Group activation requires a reason.")
            : Result.Success(Unit.Value);
    }

    private async ValueTask<Result<Unit>> AuthorizeActorAsync(
        GroupActivationRequest request,
        CancellationToken cancellationToken)
    {
        Result<UserStatusSnapshot> result = await userStatusReader
            .GetCurrentAsync(request.Actor.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Failure<Unit>(result.Error.Code, result.Error.Description);
        }

        UserStatusSnapshot actor = result.Value;
        return actor.Lifecycle == UserLifecycle.Active
            && actor.Role == SystemRole.Admin
            && actor.TokenVersion == request.Actor.TokenVersion
            ? Result.Success(Unit.Value)
            : Result.Failure<Unit>(
                "forbidden",
                "The current actor is not authorized to activate a Group.");
    }

    private async ValueTask<Result<SupplyReadinessEvidence?>> ObserveSupplyAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        Result<SupplyReadinessSnapshot> result = await supplyReadiness
            .ObserveAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return string.Equals(
                result.Error.Code,
                "group_activation_not_ready",
                StringComparison.Ordinal)
                ? Result.Success<SupplyReadinessEvidence?>(null)
                : ForwardFailure<SupplyReadinessEvidence?>(result.Error);
        }

        SupplyReadinessSnapshot readiness = result.Value;
        return Result.Success<SupplyReadinessEvidence?>(
            readiness.IsReady && !string.IsNullOrWhiteSpace(readiness.OpaqueToken)
                ? new SupplyReadinessEvidence(readiness.OpaqueToken, readiness.ObservedAt)
                : null);
    }

    private static Result<T> ForwardFailure<T>(ResultError error) => Result.Failure<T>(
        error.Code,
        error.Description,
        error.RetryAfterSeconds,
        error.ETag,
        error.Presentation);
}
