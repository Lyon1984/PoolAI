#pragma warning disable MA0048 // API Key credential and envelope DTOs are collocated with their ports.
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Application.Ports;

internal sealed record ApiKeyCredential(
    string Secret,
    string DisplayPrefix,
    byte[] Hash,
    short PepperVersion);

internal interface IApiKeyCredentialService
{
    ApiKeyCredential Create();

    bool TryGetDisplayPrefix(
        string presentedKey,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? displayPrefix);

    bool Verify(
        string presentedKey,
        byte[] expectedHash,
        short pepperVersion);
}

internal sealed record ApiKeyCreateResponseSecret(
    ApiKeyControlPlaneSnapshot ApiKey,
    string Secret,
    string ETag,
    string Location);

internal interface IApiKeyCreateResponseEnvelope
{
    JsonElement Encrypt(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId,
        IdempotencySecretBinding binding);

    ApiKeyCreateResponseSecret Decrypt(
        JsonElement envelope,
        EntityId apiKeyId,
        IdempotencySecretBinding binding);

    JsonElement EncryptRotate(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId,
        IdempotencySecretBinding binding);

    ApiKeyCreateResponseSecret DecryptRotate(
        JsonElement envelope,
        EntityId apiKeyId,
        IdempotencySecretBinding binding);
}
#pragma warning restore MA0048
