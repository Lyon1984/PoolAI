#pragma warning disable MA0048 // TOTP options are private implementation details of this authenticator.
#pragma warning disable CA5350 // RFC 6238 compatibility requires HMAC-SHA1 for the frozen R1 TOTP profile.
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class TotpAuthenticator : ITotpAuthenticator
{
    private const int SeedSize = 20;
    private const int CodeDigits = 6;
    private const int CodeModulus = 1_000_000;
    private readonly TotpOptions _options;

    internal TotpAuthenticator(TotpOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public TotpProvisioningSecret CreateProvisioningSecret(string accountName)
    {
        ValidateAccountName(accountName);
        byte[] seed = RandomNumberGenerator.GetBytes(SeedSize);
        string base32Secret;
        try
        {
            base32Secret = Base32.Encode(seed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        return new TotpProvisioningSecret(
            base32Secret,
            BuildProvisioningUri(base32Secret, accountName));
    }

    public string BuildProvisioningUri(string base32Secret, string accountName)
    {
        ValidateSeed(base32Secret);
        ValidateAccountName(accountName);
        string escapedIssuer = Uri.EscapeDataString(_options.Issuer);
        string escapedAccount = Uri.EscapeDataString(accountName);
        return string.Concat(
            "otpauth://totp/",
            escapedIssuer,
            ":",
            escapedAccount,
            "?secret=",
            base32Secret,
            "&issuer=",
            escapedIssuer,
            "&algorithm=SHA1&digits=6&period=30");
    }

    public bool TryMatchStep(
        string base32Secret,
        string code,
        DateTimeOffset timestamp,
        out long matchedStep)
    {
        matchedStep = -1;
        if (!TryReadCode(code, out byte[] submittedCode))
        {
            return false;
        }

        byte[] seed;
        try
        {
            seed = Base32.Decode(base32Secret);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            CryptographicOperations.ZeroMemory(submittedCode);
            return false;
        }

        try
        {
            if (seed.Length != SeedSize || timestamp.ToUnixTimeSeconds() < 0)
            {
                return false;
            }

            long currentStep = timestamp.ToUnixTimeSeconds() / TotpOptions.StepSeconds;
            for (int adjacent = -TotpOptions.AllowedAdjacentSteps;
                 adjacent <= TotpOptions.AllowedAdjacentSteps;
                 adjacent++)
            {
                long candidateStep = currentStep + adjacent;
                if (candidateStep < 0)
                {
                    continue;
                }

                byte[] expectedCode = CodeAtStep(seed, candidateStep);
                try
                {
                    if (CryptographicOperations.FixedTimeEquals(expectedCode, submittedCode)
                        && candidateStep > matchedStep)
                    {
                        matchedStep = candidateStep;
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expectedCode);
                }
            }

            return matchedStep >= 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
            CryptographicOperations.ZeroMemory(submittedCode);
        }
    }

    private static byte[] CodeAtStep(byte[] seed, long step)
    {
        Span<byte> counter = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] hmac = HMACSHA1.HashData(seed, counter);
        try
        {
            int offset = hmac[^1] & 0x0f;
            int binaryCode = BinaryPrimitives.ReadInt32BigEndian(hmac.AsSpan(offset, 4))
                & 0x7fff_ffff;
            int value = binaryCode % CodeModulus;
            byte[] result = new byte[CodeDigits];
            for (int index = result.Length - 1; index >= 0; index--)
            {
                result[index] = checked((byte)('0' + (value % 10)));
                value /= 10;
            }

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
            CryptographicOperations.ZeroMemory(counter);
        }
    }

    private static bool TryReadCode(string code, out byte[] value)
    {
        value = new byte[CodeDigits];
        if (code is null || code.Length != CodeDigits)
        {
            return false;
        }

        for (int index = 0; index < code.Length; index++)
        {
            char character = code[index];
            if (character is < '0' or > '9')
            {
                CryptographicOperations.ZeroMemory(value);
                return false;
            }

            value[index] = checked((byte)character);
        }

        return true;
    }

    private static void ValidateAccountName(string accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)
            || !string.Equals(accountName, accountName.Trim(), StringComparison.Ordinal)
            || accountName.Length > 254
            || accountName.Any(char.IsControl))
        {
            throw new ArgumentException("The TOTP account name is invalid.", nameof(accountName));
        }
    }

    private static void ValidateSeed(string base32Secret)
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
            if (seed.Length != SeedSize)
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

internal sealed class TotpOptions
{
    internal const int StepSeconds = 30;
    internal const int AllowedAdjacentSteps = 1;

    internal TotpOptions(string issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer)
            || !string.Equals(issuer, issuer.Trim(), StringComparison.Ordinal)
            || issuer.Length > 64
            || issuer.Any(char.IsControl))
        {
            throw new ArgumentException("The TOTP issuer is invalid.", nameof(issuer));
        }

        Issuer = issuer;
    }

    internal string Issuer { get; }

    internal static TotpOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string issuer = configuration["Auth:TOTP:Issuer"] ?? "PoolAI";
        int stepSeconds = configuration.GetValue(
            "Auth:TOTP:StepSeconds",
            StepSeconds);
        int adjacentSteps = configuration.GetValue(
            "Auth:TOTP:AllowedAdjacentSteps",
            AllowedAdjacentSteps);
        if (stepSeconds != StepSeconds || adjacentSteps != AllowedAdjacentSteps)
        {
            throw new InvalidOperationException("The frozen R1 TOTP profile is invalid.");
        }

        try
        {
            return new TotpOptions(issuer);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("The TOTP issuer configuration is invalid.", exception);
        }
    }
}
#pragma warning restore CA5350
#pragma warning restore MA0048
