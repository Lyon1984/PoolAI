using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;
using PoolAI.Modules.Identity.Domain;

namespace PoolAI.Modules.Identity.Application;

internal sealed class ApiKeyCreatedOutcomeValidator(
    IApiKeyCredentialService credentialService) : IApiKeyCreatedOutcomeValidator
{
    private readonly IApiKeyCredentialService _credentialService =
        credentialService ?? throw new ArgumentNullException(nameof(credentialService));

    public void EnsureValid(
        CreateApiKeyCommand command,
        ApiKeyCreatedOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(outcome);
        ApiKeyControlPlaneSnapshot apiKey = outcome.ApiKey
            ?? throw InvalidOutcome();
        DateTimeOffset? expiresAt = command.ExpiresAt is DateTimeOffset expiration
            ? ApiKeyInput.PostgresTimestamp(expiration)
            : null;
        string name = ApiKeyInput.Name(command.Name);
        IReadOnlyList<string> allowedCidrs =
            ApiKeyInput.AllowedCidrs(command.AllowedCidrs);
        bool validSecret = _credentialService.TryGetDisplayPrefix(
            outcome.Secret,
            out string? displayPrefix);
        string location = command.AccessMode switch
        {
            ApiKeyAccessMode.Self =>
                $"/api/v1/me/api-keys/{apiKey.ApiKeyId.Value:D}",
            ApiKeyAccessMode.AdminProxy =>
                $"/api/v1/admin/users/{command.UserId.Value:D}/api-keys/{apiKey.ApiKeyId.Value:D}",
            _ => throw InvalidOutcome(),
        };

        if (!ApiKeyResourceValidator.IsValid(ToResource(apiKey))
            || outcome.StatusCode != 201
            || apiKey.UserId != command.UserId
            || apiKey.GroupId != command.GroupId
            || !string.Equals(apiKey.Name, name, StringComparison.Ordinal)
            || apiKey.ExpiresAt != expiresAt
            || !apiKey.AllowedCidrs.SequenceEqual(
                allowedCidrs,
                StringComparer.Ordinal)
            || apiKey.Status != ApiKeyPersistentStatus.Active
            || apiKey.EffectiveStatus != ApiKeyEffectiveStatus.Active
            || apiKey.Version != 1
            || apiKey.LastUsedAt is not null
            || apiKey.CreatedAt != apiKey.UpdatedAt
            || !validSecret
            || !string.Equals(
                apiKey.Prefix,
                displayPrefix,
                StringComparison.Ordinal)
            || !string.Equals(outcome.ETag, "\"v1\"", StringComparison.Ordinal)
            || !string.Equals(outcome.Location, location, StringComparison.Ordinal))
        {
            throw InvalidOutcome();
        }
    }

    private static ApiKeyResource ToResource(ApiKeyControlPlaneSnapshot value) => new(
        value.ApiKeyId,
        value.UserId,
        value.GroupId,
        value.Name,
        value.Prefix,
        value.Status,
        value.EffectiveStatus,
        value.ExpiresAt,
        value.AllowedCidrs,
        value.LastUsedAt,
        value.Version,
        value.CreatedAt,
        value.UpdatedAt,
        value.ObservedAt);

    private static InvalidOperationException InvalidOutcome() => new(
        "The API Key create use case returned an invalid outcome.");
}
