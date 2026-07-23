namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyCreateIdempotencyPreflight
{
    ValueTask<Result<ApiKeyCreatedOutcome?>> TryReplayAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken);
}
