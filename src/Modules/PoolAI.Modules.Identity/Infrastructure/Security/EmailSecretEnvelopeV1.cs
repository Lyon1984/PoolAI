#pragma warning disable MA0048 // The envelope key-ring options are private implementation details.
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed partial class EmailSecretEnvelopeV1 : IEmailSecretEnvelope
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SchemaVersion = 1;
    private const string Algorithm = "A256GCM+A256GCM-v1";
    private static readonly HashSet<string> FieldNames = new(StringComparer.Ordinal)
    {
        "v",
        "alg",
        "kid",
        "wrapped_dek",
        "wrap_nonce",
        "wrap_tag",
        "ciphertext",
        "nonce",
        "tag",
    };

    private readonly EnvelopeKeyRingOptions _keyRing;

    internal EmailSecretEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
    }

    public PasswordResetEmailEnvelopes Encrypt(
        EmailSecretEnvelopePlaintext plaintext,
        EntityId emailOutboxId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (string.IsNullOrWhiteSpace(plaintext.Recipient)
            || string.IsNullOrWhiteSpace(plaintext.ResetUrl))
        {
            throw new ArgumentException(
                "The email secret plaintext fields are required.",
                nameof(plaintext));
        }

        return new PasswordResetEmailEnvelopes(
            EncryptField(
                plaintext.Recipient,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.RecipientField)),
            EncryptField(
                plaintext.ResetUrl,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.DeliverySecretField)));
    }

    public EmailSecretEnvelopePlaintext Decrypt(
        JsonElement recipientEnvelope,
        JsonElement deliverySecretEnvelope,
        EntityId emailOutboxId) => new(
            DecryptField(
                recipientEnvelope,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.RecipientField)),
            DecryptField(
                deliverySecretEnvelope,
                PasswordResetEmailEnvelope.BuildAad(
                    emailOutboxId,
                    PasswordResetEmailEnvelope.DeliverySecretField)));

    private JsonElement EncryptField(string plaintext, string aadText)
    {
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] aad = Encoding.UTF8.GetBytes(aadText);
        byte[] dek = RandomNumberGenerator.GetBytes(KeySize);
        byte[] contentNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] contentTag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] wrapTag = new byte[TagSize];
        byte[] wrappedDek = new byte[KeySize];
        try
        {
            using (AesGcm contentCipher = new(dek, TagSize))
            {
                contentCipher.Encrypt(
                    contentNonce,
                    plaintextBytes,
                    ciphertext,
                    contentTag,
                    aad);
            }

            using (AesGcm wrapCipher = new(_keyRing.CurrentKey, TagSize))
            {
                wrapCipher.Encrypt(
                    wrapNonce,
                    dek,
                    wrappedDek,
                    wrapTag,
                    aad);
            }

            return JsonSerializer.SerializeToElement(new EnvelopeDocument(
                SchemaVersion,
                Algorithm,
                _keyRing.CurrentKeyId,
                Base64Url.Encode(wrappedDek),
                Base64Url.Encode(wrapNonce),
                Base64Url.Encode(wrapTag),
                Base64Url.Encode(ciphertext),
                Base64Url.Encode(contentNonce),
                Base64Url.Encode(contentTag)), EnvelopeJsonContext.Default.EnvelopeDocument);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(dek);
            CryptographicOperations.ZeroMemory(wrappedDek);
        }
    }

    private string DecryptField(JsonElement envelope, string aadText)
    {
        try
        {
            ParsedEnvelope parsed = Parse(envelope);
            if (!_keyRing.DecryptKeys.TryGetValue(parsed.KeyId, out byte[]? key))
            {
                throw new CryptographicException("Unknown envelope key identifier.");
            }

            byte[] aad = Encoding.UTF8.GetBytes(aadText);
            byte[] dek = new byte[KeySize];
            byte[] plaintext = new byte[parsed.Ciphertext.Length];
            try
            {
                using (AesGcm wrapCipher = new(key, TagSize))
                {
                    wrapCipher.Decrypt(
                        parsed.WrapNonce,
                        parsed.WrappedDek,
                        parsed.WrapTag,
                        dek,
                        aad);
                }

                using (AesGcm contentCipher = new(dek, TagSize))
                {
                    contentCipher.Decrypt(
                        parsed.Nonce,
                        parsed.Ciphertext,
                        parsed.Tag,
                        plaintext,
                        aad);
                }

                return new UTF8Encoding(false, true).GetString(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is
            FormatException or InvalidOperationException or JsonException or CryptographicException
            or DecoderFallbackException)
        {
            throw new CryptographicException(
                "Email secret envelope validation failed.",
                exception);
        }
    }

    private static ParsedEnvelope Parse(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Envelope must be a JSON object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in envelope.EnumerateObject())
        {
            if (!FieldNames.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new JsonException("Envelope has an unknown or duplicate field.");
            }
        }

        if (seen.Count != FieldNames.Count
            || RequiredInt(envelope, "v") != SchemaVersion
            || !string.Equals(RequiredString(envelope, "alg"), Algorithm, StringComparison.Ordinal))
        {
            throw new JsonException("Envelope version or algorithm is not supported.");
        }

        byte[] wrappedDek = DecodeLength(envelope, "wrapped_dek", KeySize);
        byte[] wrapNonce = DecodeLength(envelope, "wrap_nonce", NonceSize);
        byte[] wrapTag = DecodeLength(envelope, "wrap_tag", TagSize);
        byte[] ciphertext = Decode(envelope, "ciphertext");
        byte[] nonce = DecodeLength(envelope, "nonce", NonceSize);
        byte[] tag = DecodeLength(envelope, "tag", TagSize);
        return new ParsedEnvelope(
            RequiredString(envelope, "kid"),
            wrappedDek,
            wrapNonce,
            wrapTag,
            ciphertext,
            nonce,
            tag);
    }

    private static int RequiredInt(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw new JsonException("Envelope integer field is invalid.");
        }

        return result;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new JsonException("Envelope string field is invalid.");
        }

        return property.GetString()!;
    }

    private static byte[] Decode(JsonElement value, string propertyName) =>
        Base64Url.Decode(RequiredString(value, propertyName));

    private static byte[] DecodeLength(JsonElement value, string propertyName, int length)
    {
        byte[] result = Decode(value, propertyName);
        if (result.Length != length)
        {
            throw new JsonException("Envelope binary field length is invalid.");
        }

        return result;
    }

    private sealed record EnvelopeDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int Version,
        [property: System.Text.Json.Serialization.JsonPropertyName("alg")] string AlgorithmName,
        [property: System.Text.Json.Serialization.JsonPropertyName("kid")] string KeyId,
        [property: System.Text.Json.Serialization.JsonPropertyName("wrapped_dek")] string WrappedDek,
        [property: System.Text.Json.Serialization.JsonPropertyName("wrap_nonce")] string WrapNonce,
        [property: System.Text.Json.Serialization.JsonPropertyName("wrap_tag")] string WrapTag,
        [property: System.Text.Json.Serialization.JsonPropertyName("ciphertext")] string Ciphertext,
        [property: System.Text.Json.Serialization.JsonPropertyName("nonce")] string Nonce,
        [property: System.Text.Json.Serialization.JsonPropertyName("tag")] string Tag);

    private sealed record ParsedEnvelope(
        string KeyId,
        byte[] WrappedDek,
        byte[] WrapNonce,
        byte[] WrapTag,
        byte[] Ciphertext,
        byte[] Nonce,
        byte[] Tag);

    [System.Text.Json.Serialization.JsonSerializable(typeof(EnvelopeDocument))]
    private sealed partial class EnvelopeJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}

internal sealed class EnvelopeKeyRingOptions
{
    internal EnvelopeKeyRingOptions(
        string currentKeyId,
        byte[] currentKey,
        IReadOnlyDictionary<string, byte[]> decryptKeys)
    {
        CurrentKeyId = currentKeyId;
        CurrentKey = currentKey;
        DecryptKeys = decryptKeys;
    }

    internal string CurrentKeyId { get; }

    internal byte[] CurrentKey { get; }

    internal IReadOnlyDictionary<string, byte[]> DecryptKeys { get; }

    internal static EnvelopeKeyRingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string currentKeyId = configuration["Secrets:Envelope:CurrentKeyId"]
            ?? throw new InvalidOperationException("Envelope current key identifier is required.");
        byte[] currentKey = ReadKey(
            configuration["Secrets:Envelope:CurrentKey"],
            "Envelope current key is invalid.");
        Dictionary<string, byte[]> ring = new(StringComparer.Ordinal);
        foreach (IConfigurationSection child in configuration
            .GetSection("Secrets:Envelope:DecryptKeyRing")
            .GetChildren())
        {
            if (!ring.TryAdd(child.Key, ReadKey(child.Value, "Envelope decrypt key is invalid.")))
            {
                throw new InvalidOperationException("Envelope decrypt key identifiers must be unique.");
            }
        }

        if (!ring.TryGetValue(currentKeyId, out byte[]? ringCurrent)
            || !CryptographicOperations.FixedTimeEquals(currentKey, ringCurrent))
        {
            throw new InvalidOperationException("Envelope decrypt key ring must contain the current key.");
        }

        return new EnvelopeKeyRingOptions(currentKeyId, currentKey, ring);
    }

    private static byte[] ReadKey(string? encoded, string message)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(message, exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(message);
        }

        return key;
    }
}
#pragma warning restore MA0048
