#pragma warning disable MA0048 // The authentication row DTO is private to this repository port.
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Application.Ports;

internal interface IApiKeyRepository
{
    ValueTask<ApiKeySlice> ListAsync(
        EntityId userId,
        ApiKeyCursor? cursor,
        int limit,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyResource?> GetAsync(
        EntityId userId,
        EntityId apiKeyId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApiKeyAuthenticationCandidate>> ListAuthenticationCandidatesAsync(
        string displayPrefix,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyCreateResult> CreateAsync(
        ApiKeyCreateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyResource?> LockAsync(
        EntityId userId,
        EntityId apiKeyId,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyUpdateResult> UpdateAsync(
        ApiKeyUpdateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyRevokeResult> RevokeAsync(
        ApiKeyRevokeWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyRotateResult> RotateAsync(
        ApiKeyRotateWrite write,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken);
}

internal sealed record ApiKeyAuthenticationCandidate(
    ApiKeyResource ApiKey,
    byte[] SecretHash,
    short PepperVersion);
#pragma warning restore MA0048
