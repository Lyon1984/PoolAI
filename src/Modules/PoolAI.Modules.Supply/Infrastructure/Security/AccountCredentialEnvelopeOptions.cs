using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Infrastructure.Secrets;

namespace PoolAI.Modules.Supply.Infrastructure.Security;

internal sealed class AccountCredentialEnvelopeOptions
{
    internal AccountCredentialEnvelopeOptions(SecretEnvelopeKeyRing keyRing)
    {
        KeyRing = keyRing ?? throw new ArgumentNullException(nameof(keyRing));
    }

    internal SecretEnvelopeKeyRing KeyRing { get; }

    internal static AccountCredentialEnvelopeOptions FromConfiguration(
        IConfiguration configuration)
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

            if (!ring.TryGetValue(currentKeyId, out byte[]? ringCurrent)
                || !CryptographicOperations.FixedTimeEquals(
                    currentKey,
                    ringCurrent))
            {
                throw new InvalidOperationException(
                    "Envelope decrypt key ring must contain the current key.");
            }

            try
            {
                return new AccountCredentialEnvelopeOptions(
                    new SecretEnvelopeKeyRing(currentKeyId, ring));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    "Envelope decrypt key ring is invalid.",
                    exception);
            }
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
