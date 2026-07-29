using System.Security.Cryptography;
using System.Text;

namespace PoolAI.Infrastructure.Secrets;

public sealed class SecretEnvelopeKeyRing
{
    public const int KeySize = 32;
    private const int MaxKeyCount = 64;
    private const int MaxKeyIdLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly Dictionary<string, byte[]> _decryptKeys =
        new(StringComparer.Ordinal);

    public SecretEnvelopeKeyRing(
        string currentKeyId,
        IReadOnlyDictionary<string, byte[]> decryptKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKeyId);
        ArgumentNullException.ThrowIfNull(decryptKeys);
        try
        {
            ValidateKeyId(currentKeyId, nameof(currentKeyId));
            if (decryptKeys.Count is < 1 or > MaxKeyCount)
            {
                throw new ArgumentException(
                    "The secret envelope key ring is not within its fixed bounds.",
                    nameof(decryptKeys));
            }

            foreach ((string keyId, byte[] key) in decryptKeys)
            {
                ValidateKeyId(keyId, nameof(decryptKeys));
                if (key is null || key.Length != KeySize)
                {
                    throw new ArgumentException(
                        "Every secret envelope key requires a bounded identifier and exactly 256 bits.",
                        nameof(decryptKeys));
                }

                if (!_decryptKeys.TryAdd(keyId, key.ToArray()))
                {
                    throw new ArgumentException(
                        "Secret envelope key identifiers must be unique.",
                        nameof(decryptKeys));
                }
            }

            if (!_decryptKeys.ContainsKey(currentKeyId))
            {
                throw new ArgumentException(
                    "The secret envelope key ring must contain its current key.",
                    nameof(decryptKeys));
            }

            EnsureDistinctKeyMaterial(nameof(decryptKeys));
            CurrentKeyId = currentKeyId;
        }
        catch
        {
            foreach (byte[] key in _decryptKeys.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }

            _decryptKeys.Clear();
            throw;
        }
    }

    public string CurrentKeyId { get; }

    internal byte[] CopyCurrentKey() => _decryptKeys[CurrentKeyId].ToArray();

    internal bool TryCopyDecryptKey(string keyId, out byte[] key)
    {
        if (_decryptKeys.TryGetValue(keyId, out byte[]? stored))
        {
            key = stored.ToArray();
            return true;
        }

        key = [];
        return false;
    }

    private static void ValidateKeyId(string keyId, string parameterName)
    {
        if (!IsCanonicalKeyId(keyId))
        {
            throw new ArgumentException(
                "Secret envelope key identifiers require bounded valid Unicode scalar values.",
                parameterName);
        }
    }

    internal static bool IsCanonicalKeyId(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)
            || keyId.Length > MaxKeyIdLength
            || keyId.Contains('\uFFFD', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            _ = StrictUtf8.GetByteCount(keyId);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private void EnsureDistinctKeyMaterial(string parameterName)
    {
        byte[][] keys = _decryptKeys.Values.ToArray();
        for (int left = 0; left < keys.Length; left++)
        {
            for (int right = left + 1; right < keys.Length; right++)
            {
                if (CryptographicOperations.FixedTimeEquals(keys[left], keys[right]))
                {
                    throw new ArgumentException(
                        "Secret envelope key material must be unique per key identifier.",
                        parameterName);
                }
            }
        }
    }
}
