namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyMutationIdempotencyPreflight
{
    ValueTask<Result<ApiKeyUpdatedOutcome?>> TryReplayUpdateAsync(
        UpdateApiKeyCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyRevokedOutcome?>> TryReplayRevokeAsync(
        RevokeApiKeyCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayRotateAsync(
        RotateApiKeyCommand command,
        CancellationToken cancellationToken);
}
