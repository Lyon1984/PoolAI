#pragma warning disable MA0048 // The private configuration adapter belongs to this envelope adapter.
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Infrastructure.Secrets;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class AeadEnvelopeV1
{
    private readonly SecretEnvelopeV1 _envelope;

    internal AeadEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        _envelope = new SecretEnvelopeV1(keyRing.KeyRing);
    }

    internal JsonElement Encrypt(ReadOnlySpan<byte> plaintext, string aadText) =>
        _envelope.Encrypt(plaintext, SecretEnvelopeContext.Parse(aadText));

    internal byte[] Decrypt(JsonElement envelope, string aadText)
    {
        try
        {
            return _envelope.Decrypt(
                envelope,
                SecretEnvelopeContext.Parse(aadText));
        }
        catch (SecretEnvelopeException exception)
        {
            throw new CryptographicException(
                "Secret envelope validation failed.",
                exception);
        }
    }
}

internal sealed class EnvelopeKeyRingOptions
{
    internal EnvelopeKeyRingOptions(
        string currentKeyId,
        byte[] currentKey,
        IReadOnlyDictionary<string, byte[]> decryptKeys)
    {
        ArgumentNullException.ThrowIfNull(currentKey);
        ArgumentNullException.ThrowIfNull(decryptKeys);
        if (!decryptKeys.TryGetValue(currentKeyId, out byte[]? ringCurrent)
            || currentKey.Length != SecretEnvelopeKeyRing.KeySize
            || !CryptographicOperations.FixedTimeEquals(currentKey, ringCurrent))
        {
            throw new InvalidOperationException(
                "Envelope decrypt key ring must contain the current key.");
        }

        try
        {
            KeyRing = new SecretEnvelopeKeyRing(currentKeyId, decryptKeys);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Envelope decrypt key ring is invalid.",
                exception);
        }

        CurrentKeyId = currentKeyId;
    }

    internal string CurrentKeyId { get; }

    internal SecretEnvelopeKeyRing KeyRing { get; }

    internal static EnvelopeKeyRingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        byte[] currentKey = [];
        Dictionary<string, byte[]> ring = new(StringComparer.Ordinal);
        try
        {
            string currentKeyId = configuration["Secrets:Envelope:CurrentKeyId"]
                ?? throw new InvalidOperationException(
                    "Envelope current key identifier is required.");
            currentKey = ReadKey(
                configuration["Secrets:Envelope:CurrentKey"],
                "Envelope current key is invalid.");
            foreach (IConfigurationSection child in configuration
                .GetSection("Secrets:Envelope:DecryptKeyRing")
                .GetChildren())
            {
                if (!ring.TryAdd(
                        child.Key,
                        ReadKey(
                            child.Value,
                            "Envelope decrypt key is invalid.")))
                {
                    throw new InvalidOperationException(
                        "Envelope decrypt key identifiers must be unique.");
                }
            }

            return new EnvelopeKeyRingOptions(currentKeyId, currentKey, ring);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentKey);
            foreach (byte[] key in ring.Values)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
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

        if (key.Length != SecretEnvelopeKeyRing.KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException(message);
        }

        return key;
    }
}
#pragma warning restore MA0048
