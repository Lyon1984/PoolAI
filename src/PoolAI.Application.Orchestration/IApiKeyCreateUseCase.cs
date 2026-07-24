using PoolAI.BuildingBlocks;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Application.Orchestration;

public interface IApiKeyCreateUseCase
{
    ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken);
}
