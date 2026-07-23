#pragma warning disable MA0051 // The mutation UoW and replay-integrity sequences are intentionally explicit.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed partial class ApiKeyUseCaseService
{
    public async ValueTask<Result<ApiKeyUpdatedOutcome?>> TryReplayUpdateAsync(
        UpdateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedUpdate prepared;
        try
        {
            prepared = PrepareUpdate(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyUpdatedOutcome?>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key update request is invalid.");
        }

        try
        {
            CommandIdempotencyAcquireResult acquire = await PreflightAcquireAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                cancellationToken).ConfigureAwait(false);
            return acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired =>
                    Result.Success<ApiKeyUpdatedOutcome?>(null),
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyUpdatedOutcome?>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyUpdatedOutcome?>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayUpdateNullable(
                        command,
                        prepared,
                        RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "update",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyUpdatedOutcome?>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyRevokedOutcome?>> TryReplayRevokeAsync(
        RevokeApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedRevoke prepared;
        try
        {
            prepared = PrepareRevoke(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyRevokedOutcome?>(
                IdentityErrorCodes.InvalidRequest,
                "The API Key revoke request is invalid.");
        }

        try
        {
            CommandIdempotencyAcquireResult acquire = await PreflightAcquireAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                cancellationToken).ConfigureAwait(false);
            return acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired =>
                    Result.Success<ApiKeyRevokedOutcome?>(null),
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyRevokedOutcome?>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyRevokedOutcome?>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayRevokeNullable(command, RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "revoke",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyRevokedOutcome?>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayRotateAsync(
        RotateApiKeyCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        PreparedRotate prepared;
        try
        {
            prepared = PrepareRotate(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyCreatedOutcome?>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key rotate request is invalid.");
        }

        try
        {
            CommandIdempotencyAcquireResult acquire = await PreflightAcquireAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                cancellationToken).ConfigureAwait(false);
            return acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired =>
                    Result.Success<ApiKeyCreatedOutcome?>(null),
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyCreatedOutcome?>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyCreatedOutcome?>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayRotateNullable(command, prepared, RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "rotate",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyCreatedOutcome?>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyUpdatedOutcome>> UpdateAsync(
        UpdateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        ApiKeyAccessDecision? accessDecision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        PreparedUpdate prepared;
        try
        {
            prepared = PrepareUpdate(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyUpdatedOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key update request is invalid.");
        }

        try
        {
            Result<Unit> snapshotValidation = ValidateMutationSnapshot(
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                snapshot);
            if (snapshotValidation.IsFailure)
            {
                return ForwardFailure<ApiKeyUpdatedOutcome>(snapshotValidation.Error);
            }

            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable unitOfWorkLease =
                unitOfWork.ConfigureAwait(false);
            CommandIdempotencyAcquireResult acquire = await AcquireMutationAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyUpdatedOutcome>? early = acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired => null,
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyUpdatedOutcome>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyUpdatedOutcome>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayUpdate(
                        command,
                        prepared,
                        RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
            if (early is not null)
            {
                return early;
            }

            CommandIdempotencyLease lease = acquire.Lease!;
            ApiKeyResource? locked = await _repository.LockAsync(
                command.UserId,
                command.ApiKeyId,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyUpdatedOutcome>? lockedFailure =
                await CheckUpdateLockedStateAsync(
                    command,
                    snapshot,
                    prepared,
                    accessDecision,
                    locked,
                    lease,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            if (lockedFailure is not null)
            {
                return lockedFailure;
            }

            ApiKeyResource before = locked!;
            ApiKeyUpdateResult update = await _repository.UpdateAsync(
                new ApiKeyUpdateWrite(
                    command.ApiKeyId,
                    command.UserId,
                    snapshot.GroupId,
                    command.ExpectedVersion,
                    snapshot.EffectiveStatus,
                    prepared.SetName,
                    prepared.Name,
                    prepared.SetStatus,
                    prepared.Status,
                    prepared.SetExpiresAt,
                    prepared.ExpiresAt,
                    prepared.SetAllowedCidrs,
                    prepared.AllowedCidrs),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (update.Disposition != ApiKeyUpdateDisposition.Updated)
            {
                MutationFailure failure = FailureForUpdate(update);
                return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                    lease,
                    failure,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            ApiKeyResource current = update.ApiKey
                ?? throw new InvalidOperationException(
                    "The API Key update omitted the current resource.");
            ValidateUpdatedResource(before, current, prepared, update.Changed);
            ApiKeyControlPlaneSnapshot result = current.ToSnapshot();
            if (update.Changed)
            {
                await AppendMutationAuditAsync(
                    command.Actor,
                    "identity.api_key.updated",
                    command.ApiKeyId,
                    command.RequestId,
                    prepared.Reason,
                    command.IpAddress,
                    command.UserAgent,
                    AuditState(before.ToSnapshot()),
                    AuditState(result),
                    command.IdempotencyKey,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
            }

            string etag = ETag(result.Version);
            await CompleteMutationSuccessAsync(
                lease,
                status: 200,
                JsonSerializer.SerializeToElement(
                    ApiKeySnapshotReplay.From(result)),
                ResponseBodyEnvelope: null,
                ETagHeaders(etag),
                result.ApiKeyId,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
            return Result.Success(new ApiKeyUpdatedOutcome(
                200,
                IsReplay: false,
                result,
                etag));
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "update",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyUpdatedOutcome>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyRevokedOutcome>> RevokeAsync(
        RevokeApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        PreparedRevoke prepared;
        try
        {
            prepared = PrepareRevoke(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyRevokedOutcome>(
                IdentityErrorCodes.InvalidRequest,
                "The API Key revoke request is invalid.");
        }

        try
        {
            Result<Unit> snapshotValidation = ValidateMutationSnapshot(
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                snapshot);
            if (snapshotValidation.IsFailure)
            {
                return ForwardFailure<ApiKeyRevokedOutcome>(snapshotValidation.Error);
            }

            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable unitOfWorkLease =
                unitOfWork.ConfigureAwait(false);
            CommandIdempotencyAcquireResult acquire = await AcquireMutationAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyRevokedOutcome>? early = acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired => null,
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyRevokedOutcome>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyRevokedOutcome>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayRevoke(command, RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
            if (early is not null)
            {
                return early;
            }

            CommandIdempotencyLease lease = acquire.Lease!;
            ApiKeyResource? locked = await _repository.LockAsync(
                command.UserId,
                command.ApiKeyId,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (locked is null)
            {
                return await CompleteMutationFailureAsync<ApiKeyRevokedOutcome>(
                    lease,
                    NotFoundFailure(),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            EnsureLockedResource(locked, command.UserId, command.ApiKeyId);
            if (locked.Status == ApiKeyPersistentStatus.Revoked)
            {
                return await CompleteMutationFailureAsync<ApiKeyRevokedOutcome>(
                    lease,
                    RevokedFailure(),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            if (locked.Version != command.ExpectedVersion)
            {
                return await CompleteMutationFailureAsync<ApiKeyRevokedOutcome>(
                    lease,
                    VersionFailure(locked.Version),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            if (locked.Version != snapshot.Version)
            {
                return await CompleteMutationFailureAsync<ApiKeyRevokedOutcome>(
                    lease,
                    ResourceConflictFailure(
                        "The API Key changed after the control-plane snapshot."),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            ApiKeyRevokeResult revoke = await _repository.RevokeAsync(
                new ApiKeyRevokeWrite(
                    command.ApiKeyId,
                    command.UserId,
                    command.ExpectedVersion,
                    prepared.Reason),
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            if (revoke.Disposition != ApiKeyRevokeDisposition.Revoked)
            {
                return await CompleteMutationFailureAsync<ApiKeyRevokedOutcome>(
                    lease,
                    FailureForRevoke(revoke),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            }

            ApiKeyResource current = revoke.ApiKey
                ?? throw new InvalidOperationException(
                    "The API Key revoke omitted the current resource.");
            ValidateRevokedResource(locked, current);
            await AppendMutationAuditAsync(
                command.Actor,
                "identity.api_key.revoked",
                command.ApiKeyId,
                command.RequestId,
                prepared.Reason,
                command.IpAddress,
                command.UserAgent,
                AuditState(locked.ToSnapshot()),
                AuditState(current.ToSnapshot()),
                command.IdempotencyKey,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);

            string etag = ETag(current.Version);
            await CompleteMutationSuccessAsync(
                lease,
                status: 204,
                ResponseBody: null,
                ResponseBodyEnvelope: null,
                ETagHeaders(etag),
                current.Id,
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
            return Result.Success(new ApiKeyRevokedOutcome(
                204,
                IsReplay: false,
                etag));
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "revoke",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyRevokedOutcome>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    public async ValueTask<Result<ApiKeyCreatedOutcome>> RotateAsync(
        RotateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        ApiKeyAccessDecision? accessDecision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(snapshot);
        PreparedRotate prepared;
        try
        {
            prepared = PrepareRotate(command);
        }
        catch (ArgumentException)
        {
            return Failure<ApiKeyCreatedOutcome>(
                IdentityErrorCodes.ValidationFailed,
                "The API Key rotate request is invalid.");
        }

        try
        {
            Result<Unit> snapshotValidation = ValidateMutationSnapshot(
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                snapshot);
            if (snapshotValidation.IsFailure)
            {
                return ForwardFailure<ApiKeyCreatedOutcome>(snapshotValidation.Error);
            }

            IUnitOfWork unitOfWork = await _unitOfWorkFactory
                .BeginAsync(cancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable unitOfWorkLease =
                unitOfWork.ConfigureAwait(false);
            CommandIdempotencyAcquireResult acquire = await AcquireMutationAsync(
                command.Actor,
                command.IdempotencyKey,
                command.RequestId,
                prepared,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyCreatedOutcome>? early = acquire.Disposition switch
            {
                CommandIdempotencyDisposition.Acquired => null,
                CommandIdempotencyDisposition.Conflict =>
                    Failure<ApiKeyCreatedOutcome>(
                        IdentityErrorCodes.IdempotencyConflict,
                        "The idempotency key was already used for a different request."),
                CommandIdempotencyDisposition.Busy =>
                    Failure<ApiKeyCreatedOutcome>(
                        IdentityErrorCodes.CoordinationUnavailable,
                        "The matching idempotent API Key command is still in progress.",
                        retryAfterSeconds: 1),
                CommandIdempotencyDisposition.Replay =>
                    ReplayRotate(command, prepared, RequireReplayResponse(acquire)),
                _ => throw new InvalidOperationException(
                    "The API Key idempotency disposition is invalid."),
            };
            if (early is not null)
            {
                return early;
            }

            CommandIdempotencyLease lease = acquire.Lease!;
            ApiKeyResource? locked = await _repository.LockAsync(
                command.UserId,
                command.ApiKeyId,
                unitOfWork.Context,
                cancellationToken).ConfigureAwait(false);
            Result<ApiKeyCreatedOutcome>? lockedFailure =
                await CheckRotateLockedStateAsync(
                    command,
                    snapshot,
                    accessDecision,
                    locked,
                    lease,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
            if (lockedFailure is not null)
            {
                return lockedFailure;
            }

            ApiKeyResource before = locked!;
            ApiKeyCredential credential = _credentialService.Create();
            try
            {
                EntityId newApiKeyId = EntityId.New();
                ApiKeyRotateResult rotate = await _repository.RotateAsync(
                    new ApiKeyRotateWrite(
                        command.ApiKeyId,
                        command.UserId,
                        snapshot.GroupId,
                        command.ExpectedVersion,
                        newApiKeyId,
                        credential.DisplayPrefix,
                        credential.Hash,
                        credential.PepperVersion,
                        prepared.Reason),
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                if (rotate.Disposition != ApiKeyRotateDisposition.Rotated)
                {
                    return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                        lease,
                        FailureForRotate(rotate),
                        unitOfWork,
                        cancellationToken).ConfigureAwait(false);
                }

                ApiKeyResource oldApiKey = rotate.OldApiKey
                    ?? throw new InvalidOperationException(
                        "The API Key rotate omitted the revoked resource.");
                ApiKeyResource newApiKey = rotate.NewApiKey
                    ?? throw new InvalidOperationException(
                        "The API Key rotate omitted the new resource.");
                ValidateRotatedResources(
                    before,
                    oldApiKey,
                    newApiKey,
                    newApiKeyId,
                    credential);
                ApiKeyControlPlaneSnapshot newSnapshot = newApiKey.ToSnapshot();
                await AppendMutationAuditAsync(
                    command.Actor,
                    "identity.api_key.rotation_source_revoked",
                    oldApiKey.Id,
                    command.RequestId,
                    prepared.Reason,
                    command.IpAddress,
                    command.UserAgent,
                    AuditState(before.ToSnapshot()),
                    AuditState(oldApiKey.ToSnapshot()),
                    command.IdempotencyKey,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);
                await AppendMutationAuditAsync(
                    command.Actor,
                    "identity.api_key.rotated",
                    newApiKey.Id,
                    command.RequestId,
                    prepared.Reason,
                    command.IpAddress,
                    command.UserAgent,
                    BeforeState: null,
                    AuditState(newSnapshot),
                    command.IdempotencyKey,
                    unitOfWork.Context,
                    cancellationToken).ConfigureAwait(false);

                string etag = ETag(newSnapshot.Version);
                string location = MutationLocation(
                    command.AccessMode,
                    command.UserId,
                    newSnapshot.ApiKeyId);
                ApiKeyCreateResponseSecret secretResponse = new(
                    newSnapshot,
                    credential.Secret,
                    etag,
                    location);
                JsonElement envelope = _responseEnvelope.EncryptRotate(
                    secretResponse,
                    newSnapshot.ApiKeyId,
                    MutationBinding(
                        command.Actor,
                        command.IdempotencyKey,
                        prepared));
                await CompleteMutationSuccessAsync(
                    lease,
                    status: 201,
                    ResponseBody: null,
                    envelope,
                    SuccessHeaders(etag, location),
                    newSnapshot.ApiKeyId,
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false);
                return Result.Success(new ApiKeyCreatedOutcome(
                    201,
                    IsReplay: false,
                    newSnapshot,
                    credential.Secret,
                    etag,
                    location));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(credential.Hash);
            }
        }
        catch (ApiKeyReplayIntegrityException exception)
        {
            await WriteMutationReplayIntegrityFailureAsync(
                "rotate",
                command.RequestId,
                command.Actor,
                command.AccessMode,
                command.UserId,
                command.ApiKeyId,
                exception,
                CancellationToken.None).ConfigureAwait(false);
            return Failure<ApiKeyCreatedOutcome>(
                IdentityErrorCodes.DependencyUnavailable,
                "The stored API Key idempotency response could not be verified.",
                retryAfterSeconds: 1);
        }
        finally
        {
            prepared.Clear();
        }
    }

    private async ValueTask<CommandIdempotencyAcquireResult> PreflightAcquireAsync(
        ApiKeyActor actor,
        string idempotencyKey,
        EntityId requestId,
        PreparedMutation prepared,
        CancellationToken cancellationToken)
    {
        IUnitOfWork unitOfWork = await _unitOfWorkFactory
            .BeginAsync(cancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable unitOfWorkLease =
            unitOfWork.ConfigureAwait(false);
        return await AcquireMutationAsync(
            actor,
            idempotencyKey,
            requestId,
            prepared,
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<CommandIdempotencyAcquireResult> AcquireMutationAsync(
        ApiKeyActor actor,
        string idempotencyKey,
        EntityId requestId,
        PreparedMutation prepared,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => _idempotencyStore.AcquireAsync(
        new CommandIdempotencyRequest(
            prepared.Scope,
            idempotencyKey,
            EntityId.New(),
            ActorFingerprint(actor),
            prepared.RequestHash,
            requestId,
            IdempotencyLease,
            IdempotencyRetention),
        unitOfWorkContext,
        cancellationToken);

    private async ValueTask<Result<ApiKeyUpdatedOutcome>?> CheckUpdateLockedStateAsync(
        UpdateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        PreparedUpdate prepared,
        ApiKeyAccessDecision? accessDecision,
        ApiKeyResource? locked,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (locked is null)
        {
            return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                lease,
                NotFoundFailure(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        EnsureLockedResource(locked, command.UserId, command.ApiKeyId);
        if (locked.Status == ApiKeyPersistentStatus.Revoked)
        {
            return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                lease,
                RevokedFailure(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (locked.GroupId != snapshot.GroupId)
        {
            return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                lease,
                ResourceConflictFailure(
                    "The API Key Group no longer matches the control-plane snapshot."),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (locked.Version != command.ExpectedVersion)
        {
            return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                lease,
                VersionFailure(locked.Version),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (locked.Version != snapshot.Version
            || locked.EffectiveStatus != snapshot.EffectiveStatus)
        {
            return await CompleteMutationFailureAsync<ApiKeyUpdatedOutcome>(
                lease,
                ResourceConflictFailure(
                    "The API Key lifecycle changed after the control-plane snapshot."),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (!UpdateRequiresSubscription(prepared, locked))
        {
            return null;
        }

        return await CompleteAccessDecisionFailureIfAnyAsync<ApiKeyUpdatedOutcome>(
            accessDecision,
            command.UserId,
            locked.GroupId,
            lease,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<ApiKeyCreatedOutcome>?> CheckRotateLockedStateAsync(
        RotateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        ApiKeyAccessDecision? accessDecision,
        ApiKeyResource? locked,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (locked is null)
        {
            return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                lease,
                NotFoundFailure(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        EnsureLockedResource(locked, command.UserId, command.ApiKeyId);
        if (locked.Status == ApiKeyPersistentStatus.Revoked)
        {
            return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                lease,
                RevokedFailure(),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (locked.GroupId != snapshot.GroupId)
        {
            return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                lease,
                ResourceConflictFailure(
                    "The API Key Group no longer matches the control-plane snapshot."),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        if (locked.Version != command.ExpectedVersion)
        {
            return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                lease,
                VersionFailure(locked.Version),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        bool expiredAtLock = locked.ExpiresAt is DateTimeOffset expiresAt
            && expiresAt <= locked.ObservedAt;
        if (locked.Version != snapshot.Version
            || locked.EffectiveStatus != snapshot.EffectiveStatus
            || expiredAtLock)
        {
            return await CompleteMutationFailureAsync<ApiKeyCreatedOutcome>(
                lease,
                ResourceConflictFailure(
                    "The API Key lifecycle does not allow credential rotation."),
                unitOfWork,
                cancellationToken).ConfigureAwait(false);
        }

        return await CompleteAccessDecisionFailureIfAnyAsync<ApiKeyCreatedOutcome>(
            accessDecision,
            command.UserId,
            locked.GroupId,
            lease,
            unitOfWork,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<T>?> CompleteAccessDecisionFailureIfAnyAsync<T>(
        ApiKeyAccessDecision? decision,
        EntityId userId,
        EntityId groupId,
        CommandIdempotencyLease lease,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        if (decision is null
            || decision.UserId != userId
            || decision.GroupId != groupId)
        {
            return Failure<T>(
                IdentityErrorCodes.DependencyUnavailable,
                "The Subscription access decision does not match the API Key target.",
                retryAfterSeconds: 1);
        }

        bool hasEvidence = decision.SubscriptionId is EntityId subscriptionId
            && subscriptionId.Value != Guid.Empty
            && decision.ObservedAt is DateTimeOffset observedAt
            && observedAt != default;
        if (decision.Kind == ApiKeyAccessDecisionKind.Authorized != hasEvidence
            || decision.Kind != ApiKeyAccessDecisionKind.Authorized
            && (decision.SubscriptionId is not null || decision.ObservedAt is not null))
        {
            return Failure<T>(
                IdentityErrorCodes.DependencyUnavailable,
                "The Subscription access decision is internally inconsistent.",
                retryAfterSeconds: 1);
        }

        return decision.Kind switch
        {
            ApiKeyAccessDecisionKind.Authorized => null,
            ApiKeyAccessDecisionKind.SubscriptionRequired =>
                await CompleteMutationFailureAsync<T>(
                    lease,
                    new MutationFailure(
                        403,
                        "subscription_required",
                        "No canonical Subscription exists for the requested Group.",
                        ETag: null),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            ApiKeyAccessDecisionKind.SubscriptionInactive =>
                await CompleteMutationFailureAsync<T>(
                    lease,
                    new MutationFailure(
                        403,
                        "subscription_inactive",
                        "The canonical Subscription is not currently active.",
                        ETag: null),
                    unitOfWork,
                    cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(decision)),
        };
    }

    private async ValueTask<Result<T>> CompleteMutationFailureAsync<T>(
        CommandIdempotencyLease lease,
        MutationFailure failure,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        ResultErrorPresentation presentation = MutationFailurePresentation(
            failure.Status,
            failure.Code);
        JsonElement headers = failure.ETag is null
            ? EmptyObject
            : ETagHeaders(failure.ETag);
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Failed,
                failure.Status,
                JsonSerializer.SerializeToElement(
                    new ReplayFailure(failure.Description, presentation)),
                ResponseBodyEnvelope: null,
                headers,
                ResourceType: null,
                ResourceId: null),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The API Key idempotency lease was lost before failure completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Failure<T>(
            failure.Code,
            failure.Description,
            retryAfterSeconds: null,
            failure.ETag,
            presentation);
    }

    private async ValueTask CompleteMutationSuccessAsync(
        CommandIdempotencyLease lease,
        int status,
        JsonElement? ResponseBody,
        JsonElement? ResponseBodyEnvelope,
        JsonElement headers,
        EntityId resourceId,
        IUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        bool completed = await _idempotencyStore.CompleteAsync(
            new CommandIdempotencyCompletion(
                lease,
                CommandIdempotencyTerminalStatus.Completed,
                status,
                ResponseBody,
                ResponseBodyEnvelope,
                headers,
                ResourceType,
                resourceId),
            unitOfWork.Context,
            cancellationToken).ConfigureAwait(false);
        if (!completed)
        {
            throw new InvalidOperationException(
                "The API Key idempotency lease was lost before completion.");
        }

        await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Result<ApiKeyUpdatedOutcome?> ReplayUpdateNullable(
        UpdateApiKeyCommand command,
        PreparedUpdate prepared,
        CommandIdempotencyResponse response)
    {
        Result<ApiKeyUpdatedOutcome> replay = ReplayUpdate(
            command,
            prepared,
            response);
        return replay.IsFailure
            ? ForwardFailure<ApiKeyUpdatedOutcome?>(replay.Error)
            : Result.Success<ApiKeyUpdatedOutcome?>(replay.Value);
    }

    private static Result<ApiKeyRevokedOutcome?> ReplayRevokeNullable(
        RevokeApiKeyCommand command,
        CommandIdempotencyResponse response)
    {
        Result<ApiKeyRevokedOutcome> replay = ReplayRevoke(command, response);
        return replay.IsFailure
            ? ForwardFailure<ApiKeyRevokedOutcome?>(replay.Error)
            : Result.Success<ApiKeyRevokedOutcome?>(replay.Value);
    }

    private Result<ApiKeyCreatedOutcome?> ReplayRotateNullable(
        RotateApiKeyCommand command,
        PreparedRotate prepared,
        CommandIdempotencyResponse response)
    {
        Result<ApiKeyCreatedOutcome> replay = ReplayRotate(command, prepared, response);
        return replay.IsFailure
            ? ForwardFailure<ApiKeyCreatedOutcome?>(replay.Error)
            : Result.Success<ApiKeyCreatedOutcome?>(replay.Value);
    }

    private static Result<ApiKeyUpdatedOutcome> ReplayUpdate(
        UpdateApiKeyCommand command,
        PreparedUpdate prepared,
        CommandIdempotencyResponse response)
    {
        try
        {
            if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
            {
                return ReplayMutationFailure<ApiKeyUpdatedOutcome>(response);
            }

            ApiKeyControlPlaneSnapshot snapshot =
                response.Body?.Deserialize<ApiKeySnapshotReplay>()?.ToSnapshot()
                ?? throw new InvalidOperationException(
                    "The API Key update replay body is invalid.");
            string? etag = Header(response.Headers, "ETag");
            if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
                || response.Status != 200
                || response.BodyEnvelope is not null
                || response.ResourceId != command.ApiKeyId
                || !string.Equals(response.ResourceType, ResourceType, StringComparison.Ordinal)
                || !HasOnlyETagHeader(response.Headers)
                || !ApiKeyResourceValidator.IsValid(ToResource(snapshot))
                || snapshot.ApiKeyId != command.ApiKeyId
                || snapshot.UserId != command.UserId
                || !IsValidUpdateReplaySnapshot(command, prepared, snapshot)
                || !string.Equals(etag, ETag(snapshot.Version), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The API Key update replay is invalid.");
            }

            return Result.Success(new ApiKeyUpdatedOutcome(
                200,
                IsReplay: true,
                snapshot,
                etag!));
        }
        catch (Exception exception) when (ShouldWrapReplayFailure(exception))
        {
            throw new ApiKeyReplayIntegrityException(response.ResourceId, exception);
        }
    }

    private static Result<ApiKeyRevokedOutcome> ReplayRevoke(
        RevokeApiKeyCommand command,
        CommandIdempotencyResponse response)
    {
        try
        {
            if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
            {
                return ReplayMutationFailure<ApiKeyRevokedOutcome>(response);
            }

            string? etag = Header(response.Headers, "ETag");
            bool hasExpectedVersion = command.ExpectedVersion < long.MaxValue
                && string.Equals(
                    etag,
                    ETag(command.ExpectedVersion + 1),
                    StringComparison.Ordinal);
            if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
                || response.Status != 204
                || response.Body is not null
                || response.BodyEnvelope is not null
                || response.ResourceId != command.ApiKeyId
                || !string.Equals(response.ResourceType, ResourceType, StringComparison.Ordinal)
                || !HasOnlyETagHeader(response.Headers)
                || !hasExpectedVersion)
            {
                throw new InvalidOperationException(
                    "The API Key revoke replay is invalid.");
            }

            return Result.Success(new ApiKeyRevokedOutcome(
                204,
                IsReplay: true,
                etag!));
        }
        catch (Exception exception) when (ShouldWrapReplayFailure(exception))
        {
            throw new ApiKeyReplayIntegrityException(response.ResourceId, exception);
        }
    }

    private Result<ApiKeyCreatedOutcome> ReplayRotate(
        RotateApiKeyCommand command,
        PreparedRotate prepared,
        CommandIdempotencyResponse response)
    {
        try
        {
            if (response.TerminalStatus == CommandIdempotencyTerminalStatus.Failed)
            {
                return ReplayMutationFailure<ApiKeyCreatedOutcome>(response);
            }

            if (response.TerminalStatus != CommandIdempotencyTerminalStatus.Completed
                || response.Status != 201
                || response.Body is not null
                || response.BodyEnvelope is null
                || response.ResourceId is null
                || !string.Equals(response.ResourceType, ResourceType, StringComparison.Ordinal)
                || !HasOnlySuccessHeaders(response.Headers)
                || !string.Equals(
                    Header(response.Headers, "Cache-Control"),
                    CacheControl,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The API Key rotate replay is invalid.");
            }

            ApiKeyCreateResponseSecret secret = _responseEnvelope.DecryptRotate(
                response.BodyEnvelope.Value,
                response.ResourceId.Value,
                MutationBinding(
                    command.Actor,
                    command.IdempotencyKey,
                    prepared));
            ApiKeyControlPlaneSnapshot apiKey = secret.ApiKey;
            bool hasValidSecret = _credentialService.TryGetDisplayPrefix(
                secret.Secret,
                out string? displayPrefix);
            string? etag = Header(response.Headers, "ETag");
            string? location = Header(response.Headers, "Location");
            if (!ApiKeyResourceValidator.IsValid(ToResource(apiKey))
                || apiKey.ApiKeyId != response.ResourceId.Value
                || apiKey.UserId != command.UserId
                || apiKey.Status != ApiKeyPersistentStatus.Active
                || apiKey.EffectiveStatus is not (
                    ApiKeyEffectiveStatus.Active or ApiKeyEffectiveStatus.Expired)
                || apiKey.Version != 1
                || apiKey.LastUsedAt is not null
                || apiKey.CreatedAt != apiKey.UpdatedAt
                || !hasValidSecret
                || !string.Equals(apiKey.Prefix, displayPrefix, StringComparison.Ordinal)
                || !string.Equals(secret.ETag, etag, StringComparison.Ordinal)
                || !string.Equals(secret.ETag, ETag(apiKey.Version), StringComparison.Ordinal)
                || !string.Equals(secret.Location, location, StringComparison.Ordinal)
                || !string.Equals(
                    secret.Location,
                    MutationLocation(
                        command.AccessMode,
                        command.UserId,
                        apiKey.ApiKeyId),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The API Key rotate replay envelope is invalid.");
            }

            return Result.Success(new ApiKeyCreatedOutcome(
                201,
                IsReplay: true,
                apiKey,
                secret.Secret,
                secret.ETag,
                secret.Location));
        }
        catch (Exception exception) when (ShouldWrapReplayFailure(exception))
        {
            throw new ApiKeyReplayIntegrityException(response.ResourceId, exception);
        }
    }

    private static bool IsValidUpdateReplaySnapshot(
        UpdateApiKeyCommand command,
        PreparedUpdate prepared,
        ApiKeyControlPlaneSnapshot snapshot)
    {
        bool validVersion = snapshot.Version == command.ExpectedVersion
            || command.ExpectedVersion < long.MaxValue
            && snapshot.Version == command.ExpectedVersion + 1;
        return validVersion
            && (!prepared.SetName
                || string.Equals(
                    snapshot.Name,
                    prepared.Name,
                    StringComparison.Ordinal))
            && (!prepared.SetStatus || snapshot.Status == prepared.Status)
            && (!prepared.SetExpiresAt
                || snapshot.ExpiresAt == prepared.ExpiresAt)
            && (!prepared.SetAllowedCidrs
                || snapshot.AllowedCidrs.SequenceEqual(
                    prepared.AllowedCidrs!,
                    StringComparer.Ordinal));
    }

    private static Result<T> ReplayMutationFailure<T>(
        CommandIdempotencyResponse response)
    {
        ReplayFailure failure = response.Body?.Deserialize<ReplayFailure>()
            ?? throw new InvalidOperationException(
                "The API Key mutation failure replay body is invalid.");
        ResultErrorPresentation presentation = failure.Presentation
            ?? throw new InvalidOperationException(
                "The API Key mutation failure presentation is missing.");
        string? etag = Header(response.Headers, "ETag");
        ResultErrorPresentation expected = MutationFailurePresentation(
            presentation.Status,
            presentation.Code);
        bool versionConflict = string.Equals(
            presentation.Code,
            IdentityErrorCodes.VersionConflict,
            StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(failure.Description)
            || response.TerminalStatus != CommandIdempotencyTerminalStatus.Failed
            || response.Status != presentation.Status
            || !PresentationsEqual(presentation, expected)
            || response.BodyEnvelope is not null
            || response.ResourceType is not null
            || response.ResourceId is not null
            || versionConflict != HasOnlyETagHeader(response.Headers)
            || versionConflict != IsCanonicalMutationETag(etag)
            || !versionConflict && !HasNoHeaders(response.Headers))
        {
            throw new InvalidOperationException(
                "The API Key mutation failure replay is invalid.");
        }

        return Result.Failure<T>(
            presentation.Code,
            failure.Description!,
            presentation.RetryAfterSeconds,
            etag,
            presentation);
    }

    private PreparedUpdate PrepareUpdate(UpdateApiKeyCommand command)
    {
        ValidateMutationCommand(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            command.RequestId,
            command.IdempotencyKey,
            command.ExpectedVersion);
        if (!command.SetName
            && !command.SetStatus
            && !command.SetExpiresAt
            && !command.SetAllowedCidrs)
        {
            throw new ArgumentException(
                "The API Key update must include a mutation field.",
                nameof(command));
        }

        string? name = command.SetName
            ? ApiKeyInput.Name(command.Name ?? string.Empty)
            : command.Name is null
                ? null
                : throw new ArgumentException(
                    "An omitted API Key name must have no value.",
                    nameof(command));
        ApiKeyPersistentStatus? status = command.SetStatus
            ? command.Status is ApiKeyPersistentStatus.Active
                or ApiKeyPersistentStatus.Disabled
                ? command.Status
                : throw new ArgumentException(
                    "The API Key update status is invalid.",
                    nameof(command))
            : command.Status is null
                ? null
                : throw new ArgumentException(
                    "An omitted API Key status must have no value.",
                    nameof(command));
        DateTimeOffset? expiresAt = command.SetExpiresAt
            ? command.ExpiresAt is DateTimeOffset expiration
                ? ApiKeyInput.PostgresTimestamp(expiration)
                : null
            : command.ExpiresAt is null
                ? null
                : throw new ArgumentException(
                    "An omitted API Key expiry must have no value.",
                    nameof(command));
        IReadOnlyList<string>? allowedCidrs = command.SetAllowedCidrs
            ? ApiKeyInput.AllowedCidrs(
                command.AllowedCidrs
                ?? throw new ArgumentException(
                    "The API Key CIDR list cannot be null.",
                    nameof(command)))
            : command.AllowedCidrs is null
                ? null
                : throw new ArgumentException(
                    "An omitted API Key CIDR list must have no value.",
                    nameof(command));
        string? reason = command.AccessMode switch
        {
            ApiKeyAccessMode.Self when command.Reason is null => null,
            ApiKeyAccessMode.AdminProxy when command.Reason is not null =>
                ApiKeyInput.AdminReason(command.Reason),
            _ => throw new ArgumentException(
                "The API Key update reason is invalid.",
                nameof(command)),
        };
        string scope = MutationScope(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            "patch",
            suffix: null);
        byte[] requestHash = HashRequest(new
        {
            user_id = command.UserId.Value,
            api_key_id = command.ApiKeyId.Value,
            expected_version = command.ExpectedVersion,
            set_name = command.SetName,
            name,
            set_status = command.SetStatus,
            status = status is ApiKeyPersistentStatus statusValue
                ? PersistentStatus(statusValue)
                : null,
            set_expires_at = command.SetExpiresAt,
            expires_at = expiresAt,
            set_allowed_cidrs = command.SetAllowedCidrs,
            allowed_cidrs = allowedCidrs,
            reason,
        });
        return new PreparedUpdate(
            command.SetName,
            name,
            command.SetStatus,
            status,
            command.SetExpiresAt,
            expiresAt,
            command.SetAllowedCidrs,
            allowedCidrs,
            reason,
            scope,
            requestHash);
    }

    private PreparedRevoke PrepareRevoke(RevokeApiKeyCommand command)
    {
        ValidateMutationCommand(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            command.RequestId,
            command.IdempotencyKey,
            command.ExpectedVersion);
        string reason = ApiKeyInput.AdminReason(command.Reason);
        string scope = MutationScope(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            "delete",
            suffix: null);
        byte[] requestHash = HashRequest(new
        {
            user_id = command.UserId.Value,
            api_key_id = command.ApiKeyId.Value,
            expected_version = command.ExpectedVersion,
            reason,
        });
        return new PreparedRevoke(reason, scope, requestHash);
    }

    private PreparedRotate PrepareRotate(RotateApiKeyCommand command)
    {
        ValidateMutationCommand(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            command.RequestId,
            command.IdempotencyKey,
            command.ExpectedVersion);
        string reason = ApiKeyInput.AdminReason(command.Reason);
        string scope = MutationScope(
            command.Actor,
            command.AccessMode,
            command.UserId,
            command.ApiKeyId,
            "post",
            "rotate");
        byte[] requestHash = HashRequest(new
        {
            user_id = command.UserId.Value,
            api_key_id = command.ApiKeyId.Value,
            expected_version = command.ExpectedVersion,
            reason,
        });
        return new PreparedRotate(reason, scope, requestHash);
    }

    private static void ValidateMutationCommand(
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId,
        EntityId requestId,
        string idempotencyKey,
        long expectedVersion)
    {
        if (!CanAccess(actor, accessMode, userId)
            || apiKeyId.Value == Guid.Empty
            || requestId.Value == Guid.Empty
            || expectedVersion <= 0)
        {
            throw new ArgumentException(
                "The API Key mutation actor or target is invalid.",
                nameof(apiKeyId));
        }

        IdentityInput.IdempotencyKey(idempotencyKey);
    }

    private static Result<Unit> ValidateMutationSnapshot(
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId,
        ApiKeyControlPlaneSnapshot snapshot)
    {
        if (!CanAccess(actor, accessMode, userId)
            || !ApiKeyResourceValidator.IsValid(ToResource(snapshot))
            || snapshot.UserId != userId
            || snapshot.ApiKeyId != apiKeyId)
        {
            return Failure<Unit>(
                IdentityErrorCodes.DependencyUnavailable,
                "The API Key control-plane snapshot is inconsistent.",
                retryAfterSeconds: 1);
        }

        return Result.Success(Unit.Value);
    }

    private static void EnsureLockedResource(
        ApiKeyResource resource,
        EntityId userId,
        EntityId apiKeyId)
    {
        if (!ApiKeyResourceValidator.IsValid(resource)
            || resource.UserId != userId
            || resource.Id != apiKeyId)
        {
            throw new InvalidOperationException(
                "The API Key repository returned an inconsistent locked resource.");
        }
    }

    private static bool UpdateRequiresSubscription(
        PreparedUpdate prepared,
        ApiKeyResource current)
    {
        ApiKeyPersistentStatus finalStatus = prepared.SetStatus
            ? prepared.Status ?? current.Status
            : current.Status;
        DateTimeOffset? finalExpiry = prepared.SetExpiresAt
            ? prepared.ExpiresAt
            : current.ExpiresAt;
        bool finalEffectiveActive = finalStatus == ApiKeyPersistentStatus.Active
            && (finalExpiry is null || finalExpiry > current.ObservedAt);
        bool enablesDisabled = current.Status == ApiKeyPersistentStatus.Disabled
            && finalStatus == ApiKeyPersistentStatus.Active;
        bool restoresExpired = current.EffectiveStatus == ApiKeyEffectiveStatus.Expired
            && finalEffectiveActive;
        return enablesDisabled || restoresExpired;
    }

    private static void ValidateUpdatedResource(
        ApiKeyResource before,
        ApiKeyResource current,
        PreparedUpdate prepared,
        bool changed)
    {
        ApiKeyResourceValidator.EnsureValid(current);
        long expectedVersion = before.Version + (changed ? 1 : 0);
        string expectedName = prepared.SetName ? prepared.Name! : before.Name;
        ApiKeyPersistentStatus expectedStatus =
            prepared.SetStatus ? prepared.Status!.Value : before.Status;
        DateTimeOffset? expectedExpiresAt =
            prepared.SetExpiresAt ? prepared.ExpiresAt : before.ExpiresAt;
        IReadOnlyList<string> expectedAllowedCidrs =
            prepared.SetAllowedCidrs ? prepared.AllowedCidrs! : before.AllowedCidrs;
        if (current.Id != before.Id
            || current.UserId != before.UserId
            || current.GroupId != before.GroupId
            || !string.Equals(current.Name, expectedName, StringComparison.Ordinal)
            || !string.Equals(current.Prefix, before.Prefix, StringComparison.Ordinal)
            || current.Status != expectedStatus
            || current.ExpiresAt != expectedExpiresAt
            || !current.AllowedCidrs.SequenceEqual(
                expectedAllowedCidrs,
                StringComparer.Ordinal)
            || current.LastUsedAt != before.LastUsedAt
            || current.Version != expectedVersion
            || current.CreatedAt != before.CreatedAt
            || changed != (current.UpdatedAt > before.UpdatedAt))
        {
            throw new InvalidOperationException(
                "The API Key repository returned an inconsistent updated resource.");
        }
    }

    private static void ValidateRevokedResource(
        ApiKeyResource before,
        ApiKeyResource current)
    {
        ApiKeyResourceValidator.EnsureValid(current);
        if (current.Id != before.Id
            || current.UserId != before.UserId
            || current.GroupId != before.GroupId
            || !string.Equals(current.Name, before.Name, StringComparison.Ordinal)
            || !string.Equals(current.Prefix, before.Prefix, StringComparison.Ordinal)
            || current.Status != ApiKeyPersistentStatus.Revoked
            || current.EffectiveStatus != ApiKeyEffectiveStatus.Revoked
            || current.ExpiresAt != before.ExpiresAt
            || !current.AllowedCidrs.SequenceEqual(
                before.AllowedCidrs,
                StringComparer.Ordinal)
            || current.LastUsedAt != before.LastUsedAt
            || current.Version != before.Version + 1
            || current.CreatedAt != before.CreatedAt
            || current.UpdatedAt <= before.UpdatedAt)
        {
            throw new InvalidOperationException(
                "The API Key repository returned an inconsistent revoked resource.");
        }
    }

    private static void ValidateRotatedResources(
        ApiKeyResource before,
        ApiKeyResource oldApiKey,
        ApiKeyResource newApiKey,
        EntityId newApiKeyId,
        ApiKeyCredential credential)
    {
        ValidateRevokedResource(before, oldApiKey);
        ApiKeyResourceValidator.EnsureValid(newApiKey);
        if (newApiKey.Id != newApiKeyId
            || newApiKey.Id == before.Id
            || newApiKey.UserId != before.UserId
            || newApiKey.GroupId != before.GroupId
            || !string.Equals(newApiKey.Name, before.Name, StringComparison.Ordinal)
            || !string.Equals(
                newApiKey.Prefix,
                credential.DisplayPrefix,
                StringComparison.Ordinal)
            || newApiKey.Status != ApiKeyPersistentStatus.Active
            || newApiKey.EffectiveStatus is not (
                ApiKeyEffectiveStatus.Active or ApiKeyEffectiveStatus.Expired)
            || newApiKey.ExpiresAt != before.ExpiresAt
            || !newApiKey.AllowedCidrs.SequenceEqual(
                before.AllowedCidrs,
                StringComparer.Ordinal)
            || newApiKey.LastUsedAt is not null
            || newApiKey.Version != 1
            || newApiKey.CreatedAt != newApiKey.UpdatedAt
            || newApiKey.CreatedAt != oldApiKey.UpdatedAt)
        {
            throw new InvalidOperationException(
                "The API Key repository returned inconsistent rotation resources.");
        }
    }

    private async ValueTask AppendMutationAuditAsync(
        ApiKeyActor actor,
        string action,
        EntityId targetId,
        EntityId requestId,
        string? reason,
        string? ipAddress,
        string? userAgent,
        JsonElement? BeforeState,
        JsonElement? AfterState,
        string idempotencyKey,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken) => await _auditAppender.AppendAsync(
        new AuditEntry(
            EntityId.New(),
            ActorType(actor.Role),
            actor.UserId,
            action,
            ResourceType,
            targetId,
            requestId,
            reason,
            ipAddress,
            userAgent,
            BeforeState,
            AfterState,
            JsonSerializer.SerializeToElement(new
            {
                idempotency_key_hash = HmacText(
                    "poolai|audit-idempotency-key|identity|v1\0",
                    idempotencyKey),
            })),
        unitOfWorkContext,
        cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteMutationReplayIntegrityFailureAsync(
        string operation,
        EntityId requestId,
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId,
        ApiKeyReplayIntegrityException exception,
        CancellationToken cancellationToken)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            operation,
            request_id = requestId.Value,
            actor_user_id = actor.UserId.Value,
            target_user_id = userId.Value,
            api_key_id = apiKeyId.Value,
            access_mode = AccessModeName(accessMode),
            response_resource_id = exception.ResponseResourceId?.Value,
        });
        await _operationalEventWriter.WriteAsync(
            ReplayIntegrityEventName,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    private static MutationFailure FailureForUpdate(ApiKeyUpdateResult result) =>
        result.Disposition switch
        {
            ApiKeyUpdateDisposition.NotFound => NotFoundFailure(),
            ApiKeyUpdateDisposition.Revoked => RevokedFailure(),
            ApiKeyUpdateDisposition.VersionConflict =>
                VersionFailure(RequireCurrentVersion(result.CurrentVersion)),
            ApiKeyUpdateDisposition.ResourceConflict =>
                ResourceConflictFailure(
                    "The API Key lifecycle or immutable Group changed."),
            ApiKeyUpdateDisposition.ValidationFailed =>
                ValidationFailure(
                    "The API Key update is no longer valid."),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

    private static MutationFailure FailureForRevoke(ApiKeyRevokeResult result) =>
        result.Disposition switch
        {
            ApiKeyRevokeDisposition.NotFound => NotFoundFailure(),
            ApiKeyRevokeDisposition.AlreadyRevoked => RevokedFailure(),
            ApiKeyRevokeDisposition.VersionConflict =>
                VersionFailure(RequireCurrentVersion(result.CurrentVersion)),
            ApiKeyRevokeDisposition.ValidationFailed => new(
                400,
                IdentityErrorCodes.InvalidRequest,
                "The API Key revoke request is invalid.",
                ETag: null),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

    private static MutationFailure FailureForRotate(ApiKeyRotateResult result) =>
        result.Disposition switch
        {
            ApiKeyRotateDisposition.NotFound => NotFoundFailure(),
            ApiKeyRotateDisposition.Revoked => RevokedFailure(),
            ApiKeyRotateDisposition.VersionConflict =>
                VersionFailure(RequireCurrentVersion(result.OldCurrentVersion)),
            ApiKeyRotateDisposition.ResourceConflict =>
                ResourceConflictFailure(
                    "The API Key lifecycle or immutable Group changed."),
            ApiKeyRotateDisposition.Conflict =>
                ResourceConflictFailure(
                    "The rotated API Key could not be created without a conflict."),
            ApiKeyRotateDisposition.ValidationFailed =>
                ValidationFailure(
                    "The API Key rotate request is no longer valid."),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };

    private static MutationFailure NotFoundFailure() => new(
        404,
        IdentityErrorCodes.ResourceNotFound,
        "The API Key does not exist.",
        ETag: null);

    private static MutationFailure RevokedFailure() => new(
        409,
        "api_key_revoked",
        "The API Key is permanently revoked.",
        ETag: null);

    private static MutationFailure ResourceConflictFailure(string description) => new(
        409,
        IdentityErrorCodes.ResourceConflict,
        description,
        ETag: null);

    private static MutationFailure ValidationFailure(string description) => new(
        422,
        IdentityErrorCodes.ValidationFailed,
        description,
        ETag: null);

    private static MutationFailure VersionFailure(long version) => new(
        412,
        IdentityErrorCodes.VersionConflict,
        "The API Key version has changed.",
        ETag(version));

    private static long RequireCurrentVersion(long? version) =>
        version is > 0
            ? version.Value
            : throw new InvalidOperationException(
                "The API Key version-conflict result omitted its current version.");

    private static ResultErrorPresentation MutationFailurePresentation(
        int status,
        string code)
    {
        (string title, string detail, bool retryable) = (code, status) switch
        {
            ("subscription_required", 403) => (
                "Subscription required",
                "No Subscription grants access to the requested Group.",
                false),
            ("subscription_inactive", 403) => (
                "Subscription inactive",
                "The Subscription does not currently grant access.",
                false),
            (IdentityErrorCodes.ResourceNotFound, 404) => (
                "Resource not found",
                "The requested resource was not found.",
                false),
            ("api_key_revoked", 409) => (
                "API Key revoked",
                "A revoked API Key cannot be restored.",
                false),
            (IdentityErrorCodes.ResourceConflict, 409) => (
                "Resource conflict",
                "The request conflicts with the current resource state.",
                false),
            (IdentityErrorCodes.VersionConflict, 412) => (
                "Version conflict",
                "The resource version no longer matches; retrieve it again before retrying.",
                true),
            (IdentityErrorCodes.ValidationFailed, 422) => (
                "Validation failed",
                "One or more request fields failed validation.",
                false),
            (IdentityErrorCodes.InvalidRequest, 400) => (
                "Invalid request",
                "The request is invalid.",
                false),
            _ => throw new InvalidOperationException(
                "The API Key mutation failure status and code are not supported."),
        };
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors =
            string.Equals(code, IdentityErrorCodes.ValidationFailed, StringComparison.Ordinal)
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["/"] = ["The request failed application validation."],
                }
                : null;
        return new ResultErrorPresentation(
            code,
            status,
            title,
            detail,
            retryable,
            RetryAfterSeconds: null,
            Errors: errors);
    }

    private static JsonElement ETagHeaders(string etag) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ETag"] = etag,
            });

    private static bool HasOnlyETagHeader(JsonElement headers)
    {
        if (headers.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonProperty[] properties = headers.EnumerateObject().ToArray();
        return properties.Length == 1
            && string.Equals(properties[0].Name, "ETag", StringComparison.Ordinal)
            && properties[0].Value.ValueKind == JsonValueKind.String;
    }

    private static bool IsCanonicalMutationETag(string? value)
    {
        if (value is null
            || value.Length < 4
            || value[0] != '"'
            || value[1] != 'v'
            || value[^1] != '"'
            || value[2] is < '1' or > '9')
        {
            return false;
        }

        return long.TryParse(
            value.AsSpan(2, value.Length - 3),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long version)
            && version > 0
            && string.Equals(value, ETag(version), StringComparison.Ordinal);
    }

    private static string MutationScope(
        ApiKeyActor actor,
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId,
        string method,
        string? suffix)
    {
        string path = accessMode switch
        {
            ApiKeyAccessMode.Self =>
                $"/api/v1/me/api-keys/{apiKeyId.Value:D}",
            ApiKeyAccessMode.AdminProxy =>
                $"/api/v1/admin/users/{userId.Value:D}/api-keys/{apiKeyId.Value:D}",
            _ => throw new ArgumentOutOfRangeException(nameof(accessMode)),
        };
        if (suffix is not null)
        {
            path = $"{path}/{suffix}";
        }

        return $"identity:{actor.UserId.Value:D}:{method}:{path}";
    }

    private static string MutationLocation(
        ApiKeyAccessMode accessMode,
        EntityId userId,
        EntityId apiKeyId) => accessMode switch
    {
        ApiKeyAccessMode.Self =>
            $"/api/v1/me/api-keys/{apiKeyId.Value:D}",
        ApiKeyAccessMode.AdminProxy =>
            $"/api/v1/admin/users/{userId.Value:D}/api-keys/{apiKeyId.Value:D}",
        _ => throw new ArgumentOutOfRangeException(nameof(accessMode)),
    };

    private static IdempotencySecretBinding MutationBinding(
        ApiKeyActor actor,
        string idempotencyKey,
        PreparedMutation prepared) => new(
        actor.UserId,
        prepared.Scope,
        idempotencyKey,
        prepared.RequestHash);

    private abstract record PreparedMutation(
        string Scope,
        byte[] RequestHash)
    {
        internal void Clear() => CryptographicOperations.ZeroMemory(RequestHash);
    }

    private sealed record PreparedUpdate(
        bool SetName,
        string? Name,
        bool SetStatus,
        ApiKeyPersistentStatus? Status,
        bool SetExpiresAt,
        DateTimeOffset? ExpiresAt,
        bool SetAllowedCidrs,
        IReadOnlyList<string>? AllowedCidrs,
        string? Reason,
        string MutationScope,
        byte[] MutationRequestHash)
        : PreparedMutation(MutationScope, MutationRequestHash);

    private sealed record PreparedRevoke(
        string Reason,
        string MutationScope,
        byte[] MutationRequestHash)
        : PreparedMutation(MutationScope, MutationRequestHash);

    private sealed record PreparedRotate(
        string Reason,
        string MutationScope,
        byte[] MutationRequestHash)
        : PreparedMutation(MutationScope, MutationRequestHash);

    private sealed record MutationFailure(
        int Status,
        string Code,
        string Description,
        string? ETag);

    private sealed record ApiKeySnapshotReplay(
        Guid ApiKeyId,
        Guid UserId,
        Guid GroupId,
        string Name,
        string Prefix,
        ApiKeyPersistentStatus Status,
        ApiKeyEffectiveStatus EffectiveStatus,
        DateTimeOffset? ExpiresAt,
        string[] AllowedCidrs,
        DateTimeOffset? LastUsedAt,
        long Version,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset ObservedAt)
    {
        internal static ApiKeySnapshotReplay From(
            ApiKeyControlPlaneSnapshot value) => new(
            value.ApiKeyId.Value,
            value.UserId.Value,
            value.GroupId.Value,
            value.Name,
            value.Prefix,
            value.Status,
            value.EffectiveStatus,
            value.ExpiresAt,
            value.AllowedCidrs.ToArray(),
            value.LastUsedAt,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt,
            value.ObservedAt);

        internal ApiKeyControlPlaneSnapshot ToSnapshot() => new(
            new EntityId(ApiKeyId),
            new EntityId(UserId),
            new EntityId(GroupId),
            Name,
            Prefix,
            Status,
            EffectiveStatus,
            ExpiresAt,
            AllowedCidrs,
            LastUsedAt,
            Version,
            CreatedAt,
            UpdatedAt,
            ObservedAt);
    }
}

#pragma warning restore MA0051
