using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Application.Orchestration;

public interface IApiKeyMutationUseCase
{
    ValueTask<Result<ApiKeyUpdatedOutcome>> UpdateAsync(
        UpdateApiKeyCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyRevokedOutcome>> RevokeAsync(
        RevokeApiKeyCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyCreatedOutcome>> RotateAsync(
        RotateApiKeyCommand command,
        CancellationToken cancellationToken);
}
