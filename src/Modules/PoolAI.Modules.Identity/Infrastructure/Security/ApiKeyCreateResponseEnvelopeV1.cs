#pragma warning disable MA0048 // The encrypted response wire contract is private to this envelope.
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using PoolAI.Modules.Identity.Abstractions;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed partial class ApiKeyCreateResponseEnvelopeV1 : IApiKeyCreateResponseEnvelope
{
    private const string CreateResponseField = "api_key_create_response";
    private const string RotateResponseField = "api_key_rotate_response";
    private readonly AeadEnvelopeV1 _envelope;

    internal ApiKeyCreateResponseEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _envelope = new AeadEnvelopeV1(keyRing);
    }

    public JsonElement Encrypt(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId,
        IdempotencySecretBinding binding) => Encrypt(
        response,
        apiKeyId,
        binding,
        CreateResponseField);

    public ApiKeyCreateResponseSecret Decrypt(
        JsonElement envelope,
        EntityId apiKeyId,
        IdempotencySecretBinding binding) => Decrypt(
        envelope,
        apiKeyId,
        binding,
        CreateResponseField);

    public JsonElement EncryptRotate(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId,
        IdempotencySecretBinding binding) => Encrypt(
        response,
        apiKeyId,
        binding,
        RotateResponseField);

    public ApiKeyCreateResponseSecret DecryptRotate(
        JsonElement envelope,
        EntityId apiKeyId,
        IdempotencySecretBinding binding) => Decrypt(
        envelope,
        apiKeyId,
        binding,
        RotateResponseField);

    private JsonElement Encrypt(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId,
        IdempotencySecretBinding binding,
        string responseField)
    {
        Validate(response, apiKeyId);
        EnvelopePayload payload = EnvelopePayload.From(response);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            ApiKeyEnvelopeJsonContext.Default.EnvelopePayload);
        try
        {
            return _envelope.Encrypt(
                plaintext,
                IdempotencyResponseAad.Build(responseField, apiKeyId, binding));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private ApiKeyCreateResponseSecret Decrypt(
        JsonElement envelope,
        EntityId apiKeyId,
        IdempotencySecretBinding binding,
        string responseField)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(
                envelope,
                IdempotencyResponseAad.Build(responseField, apiKeyId, binding));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                "API Key create-response envelope validation failed.",
                exception);
        }

        try
        {
            EnvelopePayload payload = JsonSerializer.Deserialize(
                    plaintext,
                    ApiKeyEnvelopeJsonContext.Default.EnvelopePayload)
                ?? throw new JsonException("The API Key create response is missing.");
            ApiKeyCreateResponseSecret response = payload.ToResponse();
            Validate(response, apiKeyId);
            return response;
        }
        catch (Exception exception) when (exception is
            JsonException or ArgumentException or InvalidOperationException)
        {
            throw new CryptographicException(
                "API Key create-response envelope validation failed.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(
        ApiKeyCreateResponseSecret response,
        EntityId apiKeyId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ApiKeyControlPlaneSnapshot apiKey = response.ApiKey
            ?? throw new ArgumentException(
                "The API Key create response is invalid.",
                nameof(response));
        if (apiKey.ApiKeyId != apiKeyId
            || apiKey.Version <= 0
            || apiKey.ObservedAt == default
            || apiKey.CreatedAt == default
            || apiKey.UpdatedAt == default
            || string.IsNullOrWhiteSpace(apiKey.Name)
            || string.IsNullOrWhiteSpace(apiKey.Prefix)
            || string.IsNullOrWhiteSpace(response.Secret)
            || !string.Equals(response.ETag, ETag(apiKey.Version), StringComparison.Ordinal)
            || !string.Equals(
                    response.Location,
                    Location(apiKey.UserId, apiKeyId),
                    StringComparison.Ordinal)
                && !string.Equals(
                    response.Location,
                    SelfLocation(apiKeyId),
                    StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The API Key create response is invalid.",
                nameof(response));
        }
    }

    internal static string Location(EntityId userId, EntityId apiKeyId) =>
        $"/api/v1/admin/users/{userId.Value:D}/api-keys/{apiKeyId.Value:D}";

    internal static string SelfLocation(EntityId apiKeyId) =>
        $"/api/v1/me/api-keys/{apiKeyId.Value:D}";

    private static string ETag(long version) => $"\"v{version}\"";

    private sealed record EnvelopePayload(
        [property: JsonPropertyName("api_key")] ApiKeyPayload ApiKey,
        [property: JsonPropertyName("secret")] string Secret,
        [property: JsonPropertyName("etag")] string ETag,
        [property: JsonPropertyName("location")] string Location)
    {
        internal static EnvelopePayload From(ApiKeyCreateResponseSecret response) => new(
            ApiKeyPayload.From(response.ApiKey),
            response.Secret,
            response.ETag,
            response.Location);

        internal ApiKeyCreateResponseSecret ToResponse()
        {
            if (ApiKey is null
                || string.IsNullOrWhiteSpace(Secret)
                || string.IsNullOrWhiteSpace(ETag)
                || string.IsNullOrWhiteSpace(Location))
            {
                throw new JsonException(
                    "The API Key create response is missing a required field.");
            }

            return new ApiKeyCreateResponseSecret(
                ApiKey.ToSnapshot(),
                Secret,
                ETag,
                Location);
        }
    }

    private sealed record ApiKeyPayload(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("user_id")] Guid UserId,
        [property: JsonPropertyName("group_id")] Guid GroupId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("prefix")] string Prefix,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("effective_status")] string EffectiveStatus,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("allowed_cidrs")] string[] AllowedCidrs,
        [property: JsonPropertyName("last_used_at")] DateTimeOffset? LastUsedAt,
        [property: JsonPropertyName("version")] long Version,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("observed_at")] DateTimeOffset ObservedAt)
    {
        internal static ApiKeyPayload From(ApiKeyControlPlaneSnapshot value) => new(
            value.ApiKeyId.Value,
            value.UserId.Value,
            value.GroupId.Value,
            value.Name,
            value.Prefix,
            PersistentStatus(value.Status),
            EffectiveStatusCode(value.EffectiveStatus),
            value.ExpiresAt,
            value.AllowedCidrs.ToArray(),
            value.LastUsedAt,
            value.Version,
            value.CreatedAt,
            value.UpdatedAt,
            value.ObservedAt);

        internal ApiKeyControlPlaneSnapshot ToSnapshot() => new(
            new EntityId(Id),
            new EntityId(UserId),
            new EntityId(GroupId),
            Name,
            Prefix,
            ParsePersistentStatus(Status),
            ParseEffectiveStatus(EffectiveStatus),
            ExpiresAt,
            AllowedCidrs
                ?? throw new JsonException("The API Key CIDR list is missing."),
            LastUsedAt,
            Version,
            CreatedAt,
            UpdatedAt,
            ObservedAt);
    }

    private static string PersistentStatus(ApiKeyPersistentStatus value) => value switch
    {
        ApiKeyPersistentStatus.Active => "active",
        ApiKeyPersistentStatus.Disabled => "disabled",
        ApiKeyPersistentStatus.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string EffectiveStatusCode(ApiKeyEffectiveStatus value) => value switch
    {
        ApiKeyEffectiveStatus.Active => "active",
        ApiKeyEffectiveStatus.Disabled => "disabled",
        ApiKeyEffectiveStatus.Expired => "expired",
        ApiKeyEffectiveStatus.Revoked => "revoked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ApiKeyPersistentStatus ParsePersistentStatus(string value) => value switch
    {
        "active" => ApiKeyPersistentStatus.Active,
        "disabled" => ApiKeyPersistentStatus.Disabled,
        "revoked" => ApiKeyPersistentStatus.Revoked,
        _ => throw new JsonException("The API Key persistent status is invalid."),
    };

    private static ApiKeyEffectiveStatus ParseEffectiveStatus(string value) => value switch
    {
        "active" => ApiKeyEffectiveStatus.Active,
        "disabled" => ApiKeyEffectiveStatus.Disabled,
        "expired" => ApiKeyEffectiveStatus.Expired,
        "revoked" => ApiKeyEffectiveStatus.Revoked,
        _ => throw new JsonException("The API Key effective status is invalid."),
    };

    [JsonSerializable(typeof(EnvelopePayload))]
    private sealed partial class ApiKeyEnvelopeJsonContext : JsonSerializerContext;
}
#pragma warning restore MA0048
