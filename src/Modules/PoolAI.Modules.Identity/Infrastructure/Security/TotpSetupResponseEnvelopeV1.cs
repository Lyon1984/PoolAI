using System.Buffers;
using System.Security.Cryptography;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class TotpSetupResponseEnvelopeV1 : ITotpSetupResponseEnvelope
{
    private const int FrozenExpiresInSeconds = 600;
    private static readonly HashSet<string> FieldNames = new(StringComparer.Ordinal)
    {
        "challenge_id",
        "secret",
        "otpauth_uri",
        "expires_in",
    };

    private readonly AeadEnvelopeV1 _envelope;

    internal TotpSetupResponseEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _envelope = new AeadEnvelopeV1(keyRing);
    }

    public JsonElement Encrypt(
        TotpSetupResponseSecret response,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding)
    {
        Validate(response);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("challenge_id", response.Challenge.Value);
            writer.WriteString("secret", response.Base32Secret);
            writer.WriteString("otpauth_uri", response.OtpAuthUri);
            writer.WriteNumber("expires_in", response.ExpiresInSeconds);
            writer.WriteEndObject();
        }

        byte[] plaintext = buffer.WrittenSpan.ToArray();
        try
        {
            return _envelope.Encrypt(plaintext, BuildAad(oneTimeTokenId, binding));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            buffer.Clear();
        }
    }

    public TotpSetupResponseSecret Decrypt(
        JsonElement envelope,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(envelope, BuildAad(oneTimeTokenId, binding));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                "TOTP setup-response envelope validation failed.",
                exception);
        }

        try
        {
            return ParseResponse(plaintext);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new CryptographicException(
                "TOTP setup-response envelope validation failed.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string BuildAad(
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding) => TotpIdempotencyResponseAad.Build(
            "totp_setup_response",
            oneTimeTokenId,
            binding);

    private static TotpSetupResponseSecret ParseResponse(byte[] plaintext)
    {
        using JsonDocument document = JsonDocument.Parse(plaintext);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("TOTP setup response must be a JSON object.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!FieldNames.Contains(property.Name) || !seen.Add(property.Name))
            {
                throw new JsonException("TOTP setup response has an unknown or duplicate field.");
            }
        }

        if (seen.Count != FieldNames.Count
            || !root.TryGetProperty("challenge_id", out JsonElement challengeElement)
            || challengeElement.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(challengeElement.GetString(), "D", out Guid challenge)
            || challenge == Guid.Empty
            || !root.TryGetProperty("secret", out JsonElement secretElement)
            || secretElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("otpauth_uri", out JsonElement uriElement)
            || uriElement.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("expires_in", out JsonElement expiresElement)
            || expiresElement.ValueKind != JsonValueKind.Number
            || !expiresElement.TryGetInt32(out int expiresIn))
        {
            throw new JsonException("TOTP setup response fields are invalid.");
        }

        TotpSetupResponseSecret response = new(
            new EntityId(challenge),
            secretElement.GetString()!,
            uriElement.GetString()!,
            expiresIn);
        Validate(response);
        return response;
    }

    private static void Validate(TotpSetupResponseSecret response)
    {
        ArgumentNullException.ThrowIfNull(response);
        byte[] seed;
        try
        {
            seed = Base32.Decode(response.Base32Secret);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new ArgumentException(
                "The TOTP setup seed is invalid.",
                nameof(response),
                exception);
        }

        try
        {
            if (seed.Length != 20
                || response.ExpiresInSeconds != FrozenExpiresInSeconds
                || !Uri.TryCreate(response.OtpAuthUri, UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, "otpauth", StringComparison.Ordinal)
                || !string.Equals(uri.Host, "totp", StringComparison.Ordinal)
                || !HasExpectedSecret(uri, response.Base32Secret)
                || response.OtpAuthUri.Length > 2_048)
            {
                throw new ArgumentException(
                    "The TOTP setup response is invalid.",
                    nameof(response));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    private static bool HasExpectedSecret(Uri uri, string base32Secret)
    {
        string expected = string.Concat("secret=", base32Secret);
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Count(value => string.Equals(value, expected, StringComparison.Ordinal)) == 1;
    }
}
