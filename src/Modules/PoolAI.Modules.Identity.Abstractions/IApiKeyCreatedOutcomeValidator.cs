namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyCreatedOutcomeValidator
{
    void EnsureValid(
        CreateApiKeyCommand command,
        ApiKeyCreatedOutcome outcome);
}
