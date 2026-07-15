namespace PoolAI.Modules.Identity.Abstractions;

public interface IApiKeyIssuer
{
    ValueTask<Result<IssuedApiKey>> IssueAsync(
        IssueApiKeyCommand command,
        CancellationToken cancellationToken);
}
