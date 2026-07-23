namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyMutationOwner
{
    ValueTask<Result<ApiKeyUpdatedOutcome>> UpdateAsync(
        UpdateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        ApiKeyAccessDecision? accessDecision,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyRevokedOutcome>> RevokeAsync(
        RevokeApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyCreatedOutcome>> RotateAsync(
        RotateApiKeyCommand command,
        ApiKeyControlPlaneSnapshot snapshot,
        ApiKeyAccessDecision? accessDecision,
        CancellationToken cancellationToken);
}
