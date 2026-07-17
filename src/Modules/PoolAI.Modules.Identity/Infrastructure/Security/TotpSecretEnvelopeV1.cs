using System.Security.Cryptography;
using System.Text;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class TotpSecretEnvelopeV1 : ITotpSecretEnvelope
{
    private const string Purpose = "totp-secret";
    private readonly AeadEnvelopeV1 _envelope;

    internal TotpSecretEnvelopeV1(EnvelopeKeyRingOptions keyRing)
    {
        _envelope = new AeadEnvelopeV1(keyRing);
    }

    public JsonElement Encrypt(
        string base32Secret,
        TotpSecretEnvelopeTarget target,
        EntityId targetId)
    {
        ValidateBase32Seed(base32Secret);
        byte[] plaintext = Encoding.ASCII.GetBytes(base32Secret);
        try
        {
            return _envelope.Encrypt(plaintext, BuildAad(target, targetId));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Decrypt(
        JsonElement envelope,
        TotpSecretEnvelopeTarget target,
        EntityId targetId)
    {
        byte[] plaintext;
        try
        {
            plaintext = _envelope.Decrypt(envelope, BuildAad(target, targetId));
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                "TOTP secret envelope validation failed.",
                exception);
        }

        try
        {
            string secret = new ASCIIEncoding().GetString(plaintext);
            ValidateBase32Seed(secret);
            return secret;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new CryptographicException(
                "TOTP secret envelope validation failed.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static string BuildAad(
        TotpSecretEnvelopeTarget target,
        EntityId targetId)
    {
        (string entity, string field) = target switch
        {
            TotpSecretEnvelopeTarget.SetupChallenge => ("one_time_token", "secret_envelope"),
            TotpSecretEnvelopeTarget.User => ("user", "totp_secret_envelope"),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"poolai|v1|{Purpose}|{entity}|{targetId.Value:D}|{field}");
    }

    private static void ValidateBase32Seed(string base32Secret)
    {
        byte[] seed;
        try
        {
            seed = Base32.Decode(base32Secret);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            throw new ArgumentException(
                "The TOTP seed must be canonical uppercase unpadded base32.",
                nameof(base32Secret),
                exception);
        }

        try
        {
            if (seed.Length != 20)
            {
                throw new ArgumentException(
                    "The TOTP seed must contain exactly 160 bits.",
                    nameof(base32Secret));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }
}
