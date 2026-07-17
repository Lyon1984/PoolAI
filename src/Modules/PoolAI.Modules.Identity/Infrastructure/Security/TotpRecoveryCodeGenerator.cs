#pragma warning disable MA0048 // Recovery-code pepper options are private implementation details.
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class TotpRecoveryCodeGenerator : ITotpRecoveryCodeGenerator
{
    private const int RecoveryCodeCount = 8;
    private const int RecoveryCodeSize = 10;
    private readonly TotpRecoveryCodeHashOptions _options;

    internal TotpRecoveryCodeGenerator(TotpRecoveryCodeHashOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyList<TotpRecoveryCodeSecret> CreateBatch()
    {
        List<TotpRecoveryCodeSecret> result = new(RecoveryCodeCount);
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (result.Count < RecoveryCodeCount)
        {
            byte[] secret = RandomNumberGenerator.GetBytes(RecoveryCodeSize);
            try
            {
                string code = Base32.Encode(secret);
                if (!seen.Add(code))
                {
                    continue;
                }

                result.Add(new TotpRecoveryCodeSecret(
                    code,
                    HMACSHA256.HashData(_options.Pepper, secret),
                    _options.PepperVersion));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }

        return result;
    }
}

internal sealed class TotpRecoveryCodeHashOptions
{
    internal TotpRecoveryCodeHashOptions(short pepperVersion, byte[] pepper)
    {
        if (pepperVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pepperVersion));
        }

        ArgumentNullException.ThrowIfNull(pepper);
        if (pepper.Length < 32)
        {
            throw new ArgumentException(
                "TOTP recovery-code pepper must contain at least 256 bits.",
                nameof(pepper));
        }

        PepperVersion = pepperVersion;
        Pepper = pepper;
    }

    internal short PepperVersion { get; }

    internal byte[] Pepper { get; }

    internal static TotpRecoveryCodeHashOptions FromConfiguration(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? versionText = configuration["Auth:TOTP:RecoveryCodePepperVersion"];
        string? secretText = configuration["Auth:TOTP:RecoveryCodePepper"];
        if (!short.TryParse(
                versionText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out short version)
            || version <= 0
            || string.IsNullOrWhiteSpace(secretText))
        {
            throw new InvalidOperationException(
                "TOTP recovery-code pepper configuration is invalid.");
        }

        byte[] pepper;
        try
        {
            pepper = Convert.FromBase64String(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "TOTP recovery-code pepper configuration is invalid.",
                exception);
        }

        try
        {
            return new TotpRecoveryCodeHashOptions(version, pepper);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "TOTP recovery-code pepper configuration is invalid.",
                exception);
        }
    }
}
#pragma warning restore MA0048
