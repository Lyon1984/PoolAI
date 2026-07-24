using System.Security.Cryptography;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Application;

internal sealed class ApiKeyAuthenticationService(
    IApiKeyRepository repository,
    IApiKeyCredentialService credentialService) : IApiKeyAuthenticator
{
    private readonly IApiKeyRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IApiKeyCredentialService _credentialService =
        credentialService ?? throw new ArgumentNullException(nameof(credentialService));

    public async ValueTask<Result<ApiKeyAccessSnapshot>> AuthenticateAsync(
        string presentedKey,
        CancellationToken cancellationToken)
    {
        if (!_credentialService.TryGetDisplayPrefix(
                presentedKey,
                out string? displayPrefix))
        {
            return Invalid();
        }

        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates =
            await _repository
                .ListAuthenticationCandidatesAsync(
                    displayPrefix,
                    cancellationToken)
                .ConfigureAwait(false);
        return AuthenticateCandidates(presentedKey, displayPrefix, candidates);
    }

    private Result<ApiKeyAccessSnapshot> AuthenticateCandidates(
        string presentedKey,
        string displayPrefix,
        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates)
    {
        try
        {
            if (candidates.Count > 16)
            {
                return Invalid();
            }

            ApiKeyResource? matched = FindSingleMatch(
                presentedKey,
                displayPrefix,
                candidates);
            if (!IsUsable(matched))
            {
                return Invalid();
            }

            return Result.Success(new ApiKeyAccessSnapshot(
                matched!.Id,
                matched.UserId,
                matched.GroupId,
                IsEffective: true,
                matched.Version,
                matched.ObservedAt));
        }
        finally
        {
            foreach (ApiKeyAuthenticationCandidate candidate in candidates)
            {
                CryptographicOperations.ZeroMemory(candidate.SecretHash);
            }
        }
    }

    private ApiKeyResource? FindSingleMatch(
        string presentedKey,
        string displayPrefix,
        IReadOnlyList<ApiKeyAuthenticationCandidate> candidates)
    {
        ApiKeyResource? matched = null;
        HashSet<EntityId> observedIds = [];
        foreach (ApiKeyAuthenticationCandidate candidate in candidates)
        {
            ApiKeyResource apiKey = candidate.ApiKey;
            if (!ApiKeyResourceValidator.IsValid(apiKey)
                || !string.Equals(
                    apiKey.Prefix,
                    displayPrefix,
                    StringComparison.Ordinal)
                || candidate.SecretHash.Length != 32
                || candidate.PepperVersion <= 0
                || !observedIds.Add(apiKey.Id))
            {
                return null;
            }

            if (!_credentialService.Verify(
                    presentedKey,
                    candidate.SecretHash,
                    candidate.PepperVersion))
            {
                continue;
            }

            if (matched is not null)
            {
                return null;
            }

            matched = apiKey;
        }

        return matched;
    }

    private static bool IsUsable(ApiKeyResource? apiKey) =>
        apiKey is not null
        && apiKey.Id.Value != Guid.Empty
        && apiKey.UserId.Value != Guid.Empty
        && apiKey.GroupId.Value != Guid.Empty
        && apiKey.Version > 0
        && apiKey.ObservedAt != default
        && apiKey.EffectiveStatus == ApiKeyEffectiveStatus.Active;

    private static Result<ApiKeyAccessSnapshot> Invalid() =>
        Result.Failure<ApiKeyAccessSnapshot>(
            IdentityErrorCodes.InvalidApiKey,
            "The API Key is invalid.");
}
