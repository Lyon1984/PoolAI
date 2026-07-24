#pragma warning disable MA0048 // API Key pepper options are private implementation details.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class ApiKeyCredentialService : IApiKeyCredentialService
{
    private const int PayloadSize = 32;
    private const int PayloadTextLength = 43;
    private const int DisplayPayloadLength = 8;
    private const string HashDomain = "PoolAI:ApiKey:v1:";

    private readonly string _prefix;
    private readonly ApiKeyPepper _current;
    private readonly ApiKeyPepper? _previous;

    internal ApiKeyCredentialService(ApiKeyHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _prefix = options.Prefix;
        _current = options.Current;
        _previous = options.Previous;
    }

    public ApiKeyCredential Create()
    {
        byte[] payloadBytes = RandomNumberGenerator.GetBytes(PayloadSize);
        try
        {
            string payload = Base64Url.Encode(payloadBytes);
            string secret = string.Concat(_prefix, payload);
            return new ApiKeyCredential(
                secret,
                string.Concat(_prefix, payload.AsSpan(0, DisplayPayloadLength)),
                Hash(secret, _current.Secret),
                _current.Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payloadBytes);
        }
    }

    public bool TryGetDisplayPrefix(
        string presentedKey,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? displayPrefix)
    {
        displayPrefix = null;
        if (!TryValidatePresentedKey(presentedKey, out string? payload))
        {
            return false;
        }

        displayPrefix = string.Concat(
            _prefix,
            payload.AsSpan(0, DisplayPayloadLength));
        return true;
    }

    public bool Verify(
        string presentedKey,
        byte[] expectedHash,
        short pepperVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        if (expectedHash.Length != 32
            || !TryValidatePresentedKey(presentedKey, out _))
        {
            return false;
        }

        ApiKeyPepper? pepper = pepperVersion == _current.Version
            ? _current
            : _previous?.Version == pepperVersion
                ? _previous
                : null;
        if (pepper is null)
        {
            return false;
        }

        byte[] actualHash = Hash(presentedKey, pepper.Secret);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private bool TryValidatePresentedKey(
        string value,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? payloadText)
    {
        payloadText = null;
        if (string.IsNullOrEmpty(value)
            || value.Length != _prefix.Length + PayloadTextLength
            || !value.StartsWith(_prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string candidatePayload = value[_prefix.Length..];
        byte[] payload;
        try
        {
            payload = Base64Url.Decode(candidatePayload);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (payload.Length != PayloadSize)
            {
                return false;
            }

            payloadText = candidatePayload;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static byte[] Hash(string secret, byte[] pepper)
    {
        byte[] value = Encoding.UTF8.GetBytes(HashDomain + secret);
        try
        {
            return HMACSHA256.HashData(pepper, value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}

internal sealed class ApiKeyHashOptions
{
    internal ApiKeyHashOptions(
        string prefix,
        ApiKeyPepper current,
        ApiKeyPepper? previous)
    {
        if (!IsValidPrefix(prefix))
        {
            throw new ArgumentException("The API Key prefix is invalid.", nameof(prefix));
        }

        Prefix = prefix;
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Previous = previous;
        if (previous is not null
            && (previous.Version == current.Version
                || CryptographicOperations.FixedTimeEquals(
                    previous.Secret,
                    current.Secret)))
        {
            throw new ArgumentException(
                "Current and previous API Key peppers must differ.",
                nameof(previous));
        }
    }

    internal string Prefix { get; }

    internal ApiKeyPepper Current { get; }

    internal ApiKeyPepper? Previous { get; }

    internal static ApiKeyHashOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string prefix = configuration["ApiKeys:Prefix"] ?? "sk-pool-";
        ApiKeyPepper current = ReadPepper(
            configuration,
            "Current",
            required: true,
            defaultVersion: 1)!;
        ApiKeyPepper? previous = ReadPepper(
            configuration,
            "Previous",
            required: false,
            defaultVersion: null);
        return new ApiKeyHashOptions(prefix, current, previous);
    }

    private static ApiKeyPepper? ReadPepper(
        IConfiguration configuration,
        string name,
        bool required,
        short? defaultVersion)
    {
        string versionKey = $"ApiKeys:{name}PepperVersion";
        string secretKey = $"ApiKeys:{name}Pepper";
        string? versionText = configuration[versionKey];
        string? secretText = configuration[secretKey];
        if (!required
            && string.IsNullOrWhiteSpace(versionText)
            && string.IsNullOrWhiteSpace(secretText))
        {
            return null;
        }

        short version;
        if (string.IsNullOrWhiteSpace(versionText) && defaultVersion is not null)
        {
            version = defaultVersion.Value;
        }
        else if (!short.TryParse(
                     versionText,
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out version)
                 || version <= 0)
        {
            throw new InvalidOperationException(
                $"{name} API Key pepper configuration is invalid.");
        }

        if (string.IsNullOrWhiteSpace(secretText))
        {
            throw new InvalidOperationException(
                $"{name} API Key pepper configuration is invalid.");
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{name} API Key pepper configuration is invalid.",
                exception);
        }

        if (secret.Length < 32)
        {
            throw new InvalidOperationException(
                $"{name} API Key pepper must contain at least 256 bits.");
        }

        return new ApiKeyPepper(version, secret);
    }

    private static bool IsValidPrefix(string prefix) =>
        prefix.Length is >= 5 and <= 16
        && prefix.StartsWith("sk-", StringComparison.Ordinal)
        && prefix.AsSpan(3).Length is >= 2 and <= 13
        && HasOnlyPrefixCharacters(prefix.AsSpan(3));

    private static bool HasOnlyPrefixCharacters(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed class ApiKeyPepper
{
    internal ApiKeyPepper(short version, byte[] secret)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32)
        {
            throw new ArgumentException(
                "API Key pepper must contain at least 256 bits.",
                nameof(secret));
        }

        Version = version;
        Secret = secret;
    }

    internal short Version { get; }

    internal byte[] Secret { get; }
}
#pragma warning restore MA0048
