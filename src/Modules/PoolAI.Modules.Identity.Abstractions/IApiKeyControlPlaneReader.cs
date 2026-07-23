namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyControlPlaneReader
{
    ValueTask<Result<ApiKeyPage>> ListAsync(
        ListApiKeysQuery query,
        CancellationToken cancellationToken);

    ValueTask<Result<ApiKeyControlPlaneSnapshot>> GetAsync(
        GetApiKeyQuery query,
        CancellationToken cancellationToken);
}
