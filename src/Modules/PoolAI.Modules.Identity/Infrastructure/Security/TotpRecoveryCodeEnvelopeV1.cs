using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class TotpRecoveryCodeEnvelopeV1 : ITotpRecoveryCodeEnvelope
{
    private const int RecoveryCodeCount = 8;
    private const int RecoveryCodeLength = 16;
    private readonly AeadEnvelopeV1 _envelope;

    internal TotpRecoveryCodeEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _envelope = new AeadEnvelopeV1(keyRing);
    }

    public JsonElement Encrypt(
        IReadOnlyList<string> recoveryCodes,
        EntityId oneTimeTokenId,
        IdempotencySecretBinding binding)
    {
        ValidateCodes(recoveryCodes);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("recovery_codes");
            writer.WriteStartArray();
            foreach (string code in recoveryCodes)
            {
                writer.WriteStringValue(code);
            }

            writer.WriteEndArray();
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

    public IReadOnlyList<string> Decrypt(
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
                "TOTP recovery-code envelope validation failed.",
                exception);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(plaintext);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("recovery_codes", out JsonElement codes)
                || codes.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException(
                    "Recovery-code response must contain only a recovery_codes array.");
            }

            string[] result = codes
                .EnumerateArray()
                .Select(static element => element.ValueKind == JsonValueKind.String
                    ? element.GetString()!
                    : throw new JsonException("Recovery-code values must be strings."))
                .ToArray();
            ValidateCodes(result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new CryptographicException(
                "TOTP recovery-code envelope validation failed.",
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
            "totp_confirm_response",
            oneTimeTokenId,
            binding);

    private static void ValidateCodes(IReadOnlyList<string> recoveryCodes)
    {
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        if (recoveryCodes.Count != RecoveryCodeCount
            || recoveryCodes.Any(static code => code is null || code.Length != RecoveryCodeLength)
            || recoveryCodes.Distinct(StringComparer.Ordinal).Count() != RecoveryCodeCount)
        {
            throw new ArgumentException(
                "A TOTP recovery-code batch must contain eight distinct canonical codes.",
                nameof(recoveryCodes));
        }

        foreach (string code in recoveryCodes)
        {
            byte[] decoded;
            try
            {
                decoded = Base32.Decode(code);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException(
                    "A TOTP recovery code is not canonical uppercase unpadded base32.",
                    nameof(recoveryCodes),
                    exception);
            }

            try
            {
                if (decoded.Length != 10)
                {
                    throw new ArgumentException(
                        "A TOTP recovery code must contain exactly 80 bits.",
                        nameof(recoveryCodes));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decoded);
            }
        }
    }
}
