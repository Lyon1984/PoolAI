using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolAI.Infrastructure.Secrets;

public sealed class SecretEnvelopeV1
{
    public const int SchemaVersion = 1;
    public const string Algorithm = "A256GCM+A256GCM-v1";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaxPlaintextSize = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly SecretEnvelopeKeyRing _keyRing;

    public SecretEnvelopeV1(SecretEnvelopeKeyRing keyRing)
    {
        _keyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
    }

    public JsonElement Encrypt(
        ReadOnlySpan<byte> plaintext,
        SecretEnvelopeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePlaintextBounds(plaintext);

        byte[] plaintextBytes = [], aad = [], dek = [], contentNonce = [],
            contentTag = [], ciphertext = [];
        byte[] wrapNonce = [], wrapTag = [], wrappedDek = [], wrappingKey = [];
        try
        {
            plaintextBytes = plaintext.ToArray();
            aad = StrictUtf8.GetBytes(context.CanonicalAad);
            dek = RandomNumberGenerator.GetBytes(SecretEnvelopeKeyRing.KeySize);
            contentNonce = RandomNumberGenerator.GetBytes(NonceSize);
            contentTag = new byte[TagSize];
            ciphertext = new byte[plaintextBytes.Length];
            wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            wrapTag = new byte[TagSize];
            wrappedDek = new byte[SecretEnvelopeKeyRing.KeySize];
            wrappingKey = _keyRing.CopyCurrentKey();
            EncryptContent(
                plaintextBytes,
                aad,
                dek,
                contentNonce,
                ciphertext,
                contentTag);
            WrapDek(
                dek,
                aad,
                wrappingKey,
                wrapNonce,
                wrappedDek,
                wrapTag);

            return BuildDocument(
                _keyRing.CurrentKeyId,
                wrappedDek,
                wrapNonce,
                wrapTag,
                ciphertext,
                contentNonce,
                contentTag);
        }
        finally
        {
            Clear(plaintextBytes);
            Clear(aad);
            Clear(dek);
            Clear(contentNonce);
            Clear(contentTag);
            Clear(ciphertext);
            Clear(wrapNonce);
            Clear(wrapTag);
            Clear(wrappedDek);
            Clear(wrappingKey);
        }
    }

    private static void EncryptContent(
        byte[] plaintext,
        byte[] aad,
        byte[] dek,
        byte[] nonce,
        byte[] ciphertext,
        byte[] tag)
    {
        using AesGcm contentCipher = new(dek, TagSize);
        contentCipher.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private static void ValidatePlaintextBounds(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty || plaintext.Length > MaxPlaintextSize)
        {
            throw new ArgumentException(
                "Secret envelope plaintext is outside the supported bounds.",
                nameof(plaintext));
        }
    }

    private static void WrapDek(
        byte[] dek,
        byte[] aad,
        byte[] wrappingKey,
        byte[] nonce,
        byte[] wrappedDek,
        byte[] tag)
    {
        using AesGcm wrapCipher = new(wrappingKey, TagSize);
        wrapCipher.Encrypt(nonce, dek, wrappedDek, tag, aad);
    }

    public byte[] Decrypt(
        JsonElement envelope,
        SecretEnvelopeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using ParsedEnvelope parsed = Parse(envelope);
        byte[] aad = [];
        byte[] dek = [];
        try
        {
            aad = StrictUtf8.GetBytes(context.CanonicalAad);
            dek = UnwrapDek(parsed, aad);
            return DecryptContent(parsed, dek, aad);
        }
        finally
        {
            Clear(dek);
            Clear(aad);
        }
    }

    public SecretEnvelopeMetadata Inspect(
        JsonElement envelope,
        SecretEnvelopeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using ParsedEnvelope parsed = Parse(envelope);
        byte[] aad = [];
        byte[] dek = [];
        byte[] plaintext = [];
        try
        {
            aad = StrictUtf8.GetBytes(context.CanonicalAad);
            dek = UnwrapDek(parsed, aad);
            plaintext = DecryptContent(parsed, dek, aad);
            return Metadata(parsed.KeyId);
        }
        finally
        {
            Clear(dek);
            Clear(plaintext);
            Clear(aad);
        }
    }

    public SecretEnvelopeRewrapResult Rewrap(
        JsonElement envelope,
        SecretEnvelopeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using ParsedEnvelope parsed = Parse(envelope);
        byte[] aad = [];
        byte[] dek = [];
        byte[] plaintext = [];
        try
        {
            aad = StrictUtf8.GetBytes(context.CanonicalAad);
            dek = UnwrapDek(parsed, aad);
            plaintext = DecryptContent(parsed, dek, aad);
            if (string.Equals(
                    parsed.KeyId,
                    _keyRing.CurrentKeyId,
                    StringComparison.Ordinal))
            {
                return new SecretEnvelopeRewrapResult(
                    envelope.Clone(),
                    Metadata(parsed.KeyId),
                    parsed.KeyId,
                    Changed: false);
            }

            return RewrapDek(parsed, dek, aad);
        }
        finally
        {
            Clear(dek);
            Clear(plaintext);
            Clear(aad);
        }
    }

    private SecretEnvelopeRewrapResult RewrapDek(
        ParsedEnvelope parsed,
        byte[] dek,
        byte[] aad)
    {
        byte[] wrappingKey = [];
        byte[] wrapNonce = [];
        byte[] wrapTag = [];
        byte[] wrappedDek = [];
        try
        {
            wrappingKey = _keyRing.CopyCurrentKey();
            wrapNonce = RandomNumberGenerator.GetBytes(NonceSize);
            wrapTag = new byte[TagSize];
            wrappedDek = new byte[SecretEnvelopeKeyRing.KeySize];
            using (AesGcm wrapCipher = new(wrappingKey, TagSize))
            {
                wrapCipher.Encrypt(
                    wrapNonce,
                    dek,
                    wrappedDek,
                    wrapTag,
                    aad);
            }

            JsonElement rewrapped = BuildDocument(
                _keyRing.CurrentKeyId,
                wrappedDek,
                wrapNonce,
                wrapTag,
                parsed.Ciphertext,
                parsed.Nonce,
                parsed.Tag);
            return new SecretEnvelopeRewrapResult(
                rewrapped,
                Metadata(_keyRing.CurrentKeyId),
                parsed.KeyId,
                Changed: true);
        }
        finally
        {
            Clear(wrappingKey);
            Clear(wrapNonce);
            Clear(wrapTag);
            Clear(wrappedDek);
        }
    }

    private byte[] UnwrapDek(ParsedEnvelope parsed, byte[] aad)
    {
        if (!_keyRing.TryCopyDecryptKey(parsed.KeyId, out byte[] key))
        {
            throw new SecretEnvelopeException(SecretEnvelopeFailure.UnknownKey);
        }

        byte[] dek = [];
        try
        {
            dek = new byte[SecretEnvelopeKeyRing.KeySize];
            using AesGcm wrapCipher = new(key, TagSize);
            wrapCipher.Decrypt(
                parsed.WrapNonce,
                parsed.WrappedDek,
                parsed.WrapTag,
                dek,
                aad);
            return dek;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new SecretEnvelopeException(
                SecretEnvelopeFailure.AuthenticationFailed,
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DecryptContent(
        ParsedEnvelope parsed,
        byte[] dek,
        byte[] aad)
    {
        byte[] plaintext = new byte[parsed.Ciphertext.Length];
        try
        {
            using AesGcm contentCipher = new(dek, TagSize);
            contentCipher.Decrypt(
                parsed.Nonce,
                parsed.Ciphertext,
                parsed.Tag,
                plaintext,
                aad);
            return plaintext;
        }
        catch (CryptographicException exception)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SecretEnvelopeException(
                SecretEnvelopeFailure.AuthenticationFailed,
                exception);
        }
    }

    private static ParsedEnvelope Parse(JsonElement envelope)
    {
        try
        {
            if (envelope.ValueKind != JsonValueKind.Object)
            {
                throw InvalidDocument();
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in envelope.EnumerateObject())
            {
                if (!IsFieldName(property.Name) || !seen.Add(property.Name))
                {
                    throw InvalidDocument();
                }
            }

            if (seen.Count != 9)
            {
                throw InvalidDocument();
            }

            int version = RequiredInt(envelope, "v");
            if (version != SchemaVersion)
            {
                throw new SecretEnvelopeException(
                    SecretEnvelopeFailure.UnsupportedVersion);
            }

            string algorithm = RequiredString(envelope, "alg");
            if (!string.Equals(algorithm, Algorithm, StringComparison.Ordinal))
            {
                throw new SecretEnvelopeException(
                    SecretEnvelopeFailure.UnsupportedAlgorithm);
            }

            string keyId = RequiredString(envelope, "kid");
            if (!SecretEnvelopeKeyRing.IsCanonicalKeyId(keyId))
            {
                throw InvalidDocument();
            }

            return ParseBinaryFields(envelope, keyId);
        }
        catch (SecretEnvelopeException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            FormatException or InvalidOperationException or JsonException)
        {
            throw new SecretEnvelopeException(
                SecretEnvelopeFailure.InvalidDocument,
                exception);
        }
    }

    private static int RequiredInt(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out int result))
        {
            throw InvalidDocument();
        }

        return result;
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw InvalidDocument();
        }

        return property.GetString()!;
    }

    private static ParsedEnvelope ParseBinaryFields(
        JsonElement envelope,
        string keyId)
    {
        byte[]? wrappedDek = null;
        byte[]? wrapNonce = null;
        byte[]? wrapTag = null;
        byte[]? ciphertext = null;
        byte[]? nonce = null;
        byte[]? tag = null;
        try
        {
            wrappedDek = DecodeLength(
                envelope,
                "wrapped_dek",
                SecretEnvelopeKeyRing.KeySize);
            wrapNonce = DecodeLength(envelope, "wrap_nonce", NonceSize);
            wrapTag = DecodeLength(envelope, "wrap_tag", TagSize);
            ciphertext = DecodeCiphertext(envelope);
            nonce = DecodeLength(envelope, "nonce", NonceSize);
            tag = DecodeLength(envelope, "tag", TagSize);
            return new ParsedEnvelope(
                keyId,
                wrappedDek,
                wrapNonce,
                wrapTag,
                ciphertext,
                nonce,
                tag);
        }
        catch
        {
            Clear(wrappedDek);
            Clear(wrapNonce);
            Clear(wrapTag);
            Clear(ciphertext);
            Clear(nonce);
            Clear(tag);
            throw;
        }
    }

    private static byte[] DecodeCiphertext(JsonElement value)
    {
        string encoded = RequiredString(value, "ciphertext");
        if (encoded.Length > MaxBase64UrlLength(MaxPlaintextSize))
        {
            throw InvalidDocument();
        }

        byte[] result = Base64Url.Decode(encoded);
        if (result.Length is > 0 and <= MaxPlaintextSize)
        {
            return result;
        }

        Clear(result);
        throw InvalidDocument();
    }

    private static byte[] DecodeLength(
        JsonElement value,
        string propertyName,
        int expectedLength)
    {
        string encoded = RequiredString(value, propertyName);
        if (encoded.Length != MaxBase64UrlLength(expectedLength))
        {
            throw InvalidDocument();
        }

        byte[] result = Base64Url.Decode(encoded);
        if (result.Length == expectedLength)
        {
            return result;
        }

        CryptographicOperations.ZeroMemory(result);
        throw InvalidDocument();
    }

    private static int MaxBase64UrlLength(int byteLength) =>
        checked((byteLength * 8 + 5) / 6);

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static bool IsFieldName(string name) => name is
        "v"
        or "alg"
        or "kid"
        or "wrapped_dek"
        or "wrap_nonce"
        or "wrap_tag"
        or "ciphertext"
        or "nonce"
        or "tag";

    private static SecretEnvelopeException InvalidDocument() =>
        new(SecretEnvelopeFailure.InvalidDocument);

    private static SecretEnvelopeMetadata Metadata(string keyId) =>
        new(SchemaVersion, Algorithm, keyId);

    private static JsonElement BuildDocument(
        string keyId,
        byte[] wrappedDek,
        byte[] wrapNonce,
        byte[] wrapTag,
        byte[] ciphertext,
        byte[] nonce,
        byte[] tag)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SchemaVersion);
            writer.WriteString("alg", Algorithm);
            writer.WriteString("kid", keyId);
            writer.WriteString("wrapped_dek", Base64Url.Encode(wrappedDek));
            writer.WriteString("wrap_nonce", Base64Url.Encode(wrapNonce));
            writer.WriteString("wrap_tag", Base64Url.Encode(wrapTag));
            writer.WriteString("ciphertext", Base64Url.Encode(ciphertext));
            writer.WriteString("nonce", Base64Url.Encode(nonce));
            writer.WriteString("tag", Base64Url.Encode(tag));
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed class ParsedEnvelope : IDisposable
    {
        internal ParsedEnvelope(
            string keyId,
            byte[] wrappedDek,
            byte[] wrapNonce,
            byte[] wrapTag,
            byte[] ciphertext,
            byte[] nonce,
            byte[] tag)
        {
            KeyId = keyId;
            WrappedDek = wrappedDek;
            WrapNonce = wrapNonce;
            WrapTag = wrapTag;
            Ciphertext = ciphertext;
            Nonce = nonce;
            Tag = tag;
        }

        internal string KeyId { get; }

        internal byte[] WrappedDek { get; }

        internal byte[] WrapNonce { get; }

        internal byte[] WrapTag { get; }

        internal byte[] Ciphertext { get; }

        internal byte[] Nonce { get; }

        internal byte[] Tag { get; }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(WrappedDek);
            CryptographicOperations.ZeroMemory(WrapNonce);
            CryptographicOperations.ZeroMemory(WrapTag);
            CryptographicOperations.ZeroMemory(Ciphertext);
            CryptographicOperations.ZeroMemory(Nonce);
            CryptographicOperations.ZeroMemory(Tag);
        }
    }
}
