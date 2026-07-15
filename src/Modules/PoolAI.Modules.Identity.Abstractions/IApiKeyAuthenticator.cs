namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyAuthenticator
{
    ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
        string presentedKey,
        CancellationToken cancellationToken);
}
