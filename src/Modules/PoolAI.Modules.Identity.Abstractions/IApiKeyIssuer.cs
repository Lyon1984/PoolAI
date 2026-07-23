namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyIssuer
{
    ValueTask<Result<ApiKeyCreatedOutcome>> CreateAsync(
        CreateApiKeyCommand command,
        ApiKeyAccessDecision accessDecision,
        CancellationToken cancellationToken);
}
