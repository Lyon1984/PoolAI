#pragma warning disable MA0048 // Refresh pepper options are private implementation details of this hasher.
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class RefreshCredentialHasher : IRefreshCredentialHasher
{
    private const int CredentialSize = 32;
    private const int HashSize = 32;
    private readonly RefreshTokenPepper _current;
    private readonly RefreshTokenPepper? _previous;

    internal RefreshCredentialHasher(RefreshTokenHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _current = options.Current;
        _previous = options.Previous;
    }

    public RefreshCredentialSecret Create()
    {
        byte[] credential = RandomNumberGenerator.GetBytes(CredentialSize);
        try
        {
            return new RefreshCredentialSecret(
                Base64Url.Encode(credential),
                Hash(credential, _current.Secret),
                _current.Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    public IReadOnlyList<RefreshCredentialCandidate> HashCandidates(string token)
    {
        byte[] credential;
        try
        {
            credential = Base64Url.Decode(token);
        }
        catch (FormatException)
        {
            return Array.Empty<RefreshCredentialCandidate>();
        }

        try
        {
            if (credential.Length != CredentialSize)
            {
                return Array.Empty<RefreshCredentialCandidate>();
            }

            List<RefreshCredentialCandidate> candidates = new(2)
            {
                new(Hash(credential, _current.Secret), _current.Version),
            };
            if (_previous is not null)
            {
                candidates.Add(new RefreshCredentialCandidate(
                    Hash(credential, _previous.Secret),
                    _previous.Version));
            }

            return candidates;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    public bool Verify(string token, byte[] expectedHash, short pepperVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        RefreshTokenPepper? pepper = PepperForVersion(pepperVersion);
        if (pepper is null || expectedHash.Length != HashSize)
        {
            return false;
        }

        byte[] credential;
        try
        {
            credential = Base64Url.Decode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            if (credential.Length != CredentialSize)
            {
                return false;
            }

            byte[] actualHash = Hash(credential, pepper.Secret);
            try
            {
                return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualHash);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    private RefreshTokenPepper? PepperForVersion(short pepperVersion)
    {
        if (_current.Version == pepperVersion)
        {
            return _current;
        }

        return _previous?.Version == pepperVersion ? _previous : null;
    }

    private static byte[] Hash(ReadOnlySpan<byte> credential, byte[] pepper) =>
        HMACSHA256.HashData(pepper, credential);
}

internal sealed class RefreshTokenHashOptions
{
    internal RefreshTokenHashOptions(
        RefreshTokenPepper current,
        RefreshTokenPepper? previous)
    {
        Current = current ?? throw new ArgumentNullException(nameof(current));
        Previous = previous;
        if (previous is not null && previous.Version == current.Version)
        {
            throw new ArgumentException(
                "Current and previous refresh pepper versions must differ.",
                nameof(previous));
        }
    }

    internal RefreshTokenPepper Current { get; }

    internal RefreshTokenPepper? Previous { get; }

    internal static RefreshTokenHashOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        RefreshTokenPepper current = ReadPepper(configuration, "Current", required: true)!;
        RefreshTokenPepper? previous = ReadPepper(configuration, "Previous", required: false);
        return new RefreshTokenHashOptions(current, previous);
    }

    private static RefreshTokenPepper? ReadPepper(
        IConfiguration configuration,
        string name,
        bool required)
    {
        string versionKey = $"Auth:RefreshToken:{name}PepperVersion";
        string secretKey = $"Auth:RefreshToken:{name}Pepper";
        string? versionText = configuration[versionKey];
        string? secretText = configuration[secretKey];
        if (!required && string.IsNullOrWhiteSpace(versionText) && string.IsNullOrWhiteSpace(secretText))
        {
            return null;
        }

        if (!short.TryParse(
                versionText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out short version)
            || version <= 0
            || string.IsNullOrWhiteSpace(secretText))
        {
            throw new InvalidOperationException($"{name} refresh token pepper configuration is invalid.");
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{name} refresh token pepper configuration is invalid.",
                exception);
        }

        if (secret.Length < 32)
        {
            throw new InvalidOperationException(
                $"{name} refresh token pepper must contain at least 256 bits.");
        }

        return new RefreshTokenPepper(version, secret);
    }
}

internal sealed class RefreshTokenPepper
{
    internal RefreshTokenPepper(short version, byte[] secret)
    {
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length < 32)
        {
            throw new ArgumentException(
                "Refresh token pepper must contain at least 256 bits.",
                nameof(secret));
        }

        Version = version;
        Secret = secret;
    }

    internal short Version { get; }

    internal byte[] Secret { get; }
}
#pragma warning restore MA0048
