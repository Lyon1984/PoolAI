using PoolAI.BuildingBlocks;
using PoolAI.Modules.GroupQuota.Abstractions;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Application.Orchestration;

public sealed class GroupActivationOrchestrator(
    IUserStatusReader userStatusReader,
    IGroupStatusReader groupStatusReader,
    IGroupSupplyReadiness supplyReadiness,
    IGroupActivationCommand activationCommand)
{
    public async ValueTask<Result<GroupActivationResult>> ActivateAsync(
        GroupActivationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Result<Unit> requestValidation = ValidateRequest(request);
        if (requestValidation.IsFailure)
        {
            return Result.Failure<GroupActivationResult>(
                requestValidation.Error.Code,
                requestValidation.Error.Description);
        }

        Result<Unit> authorization = await AuthorizeActorAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (authorization.IsFailure)
        {
            return Result.Failure<GroupActivationResult>(
                authorization.Error.Code,
                authorization.Error.Description);
        }

        Result<Unit> groupValidation = await ValidateGroupAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (groupValidation.IsFailure)
        {
            return Result.Failure<GroupActivationResult>(
                groupValidation.Error.Code,
                groupValidation.Error.Description);
        }

        Result<SupplyReadinessSnapshot> readinessResult = await ObserveReadySupplyAsync(
            request.GroupId,
            cancellationToken)
            .ConfigureAwait(false);
        if (readinessResult.IsFailure)
        {
            return Result.Failure<GroupActivationResult>(
                readinessResult.Error.Code,
                readinessResult.Error.Description);
        }

        SupplyReadinessSnapshot readiness = readinessResult.Value;
        ActivateGroupCommand command = new(
            request.Actor,
            request.GroupId,
            request.ExpectedGroupVersion,
            request.IdempotencyKey,
            request.Reason,
            new SupplyReadinessEvidence(
                readiness.OpaqueToken,
                readiness.ObservedAt));

        return await activationCommand
            .ActivateAsync(command, cancellationToken)
            .ConfigureAwait(false);
    }

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

    private async ValueTask<Result<Unit>> ValidateGroupAsync(
        GroupActivationRequest request,
        CancellationToken cancellationToken)
    {
        Result<GroupSnapshot> result = await groupStatusReader
            .GetAsync(request.GroupId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Failure<Unit>(result.Error.Code, result.Error.Description);
        }

        GroupSnapshot group = result.Value;
        if (group.Version != request.ExpectedGroupVersion)
        {
            return Result.Failure<Unit>(
                "version_conflict",
                "The Group version changed before activation.");
        }

        return group.Lifecycle == GroupLifecycle.Disabled && group.HasCurrentQuotaPeriod
            ? Result.Success(Unit.Value)
            : Result.Failure<Unit>(
                "group_activation_not_ready",
                "Only a disabled Group with a current quota period can be activated.");
    }

    private async ValueTask<Result<SupplyReadinessSnapshot>> ObserveReadySupplyAsync(
        EntityId groupId,
        CancellationToken cancellationToken)
    {
        Result<SupplyReadinessSnapshot> result = await supplyReadiness
            .ObserveAsync(groupId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result;
        }

        SupplyReadinessSnapshot readiness = result.Value;
        return readiness.IsReady && !string.IsNullOrWhiteSpace(readiness.OpaqueToken)
            ? result
            : Result.Failure<SupplyReadinessSnapshot>(
                "group_activation_not_ready",
                "The current Supply Configuration is not ready for activation.");
    }
}
