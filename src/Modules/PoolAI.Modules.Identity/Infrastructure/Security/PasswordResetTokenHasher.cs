#pragma warning disable MA0048 // The key-ring options are private implementation details of this hasher.
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class PasswordResetTokenHasher : IPasswordResetTokenHasher
{
    private readonly TokenPepper _current;
    private readonly TokenPepper? _previous;

    internal PasswordResetTokenHasher(TokenHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _current = options.Current;
        _previous = options.Previous;
    }

    public PasswordResetTokenSecret Create()
    {
        byte[] tokenBytes = RandomNumberGenerator.GetBytes(32);
        string token = Base64Url.Encode(tokenBytes);
        byte[] hash = Hash(tokenBytes, _current.Secret);
        CryptographicOperations.ZeroMemory(tokenBytes);
        return new PasswordResetTokenSecret(token, hash, _current.Version);
    }

    public IReadOnlyList<PasswordResetTokenCandidate> HashCandidates(string token)
    {
        byte[] tokenBytes;
        try
        {
            tokenBytes = Base64Url.Decode(token);
        }
        catch (FormatException)
        {
            return Array.Empty<PasswordResetTokenCandidate>();
        }

        if (tokenBytes.Length != 32)
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            return Array.Empty<PasswordResetTokenCandidate>();
        }

        List<PasswordResetTokenCandidate> candidates = new(2)
        {
            new(Hash(tokenBytes, _current.Secret), _current.Version),
        };
        if (_previous is not null)
        {
            candidates.Add(new PasswordResetTokenCandidate(
                Hash(tokenBytes, _previous.Secret),
                _previous.Version));
        }

        CryptographicOperations.ZeroMemory(tokenBytes);
        return candidates;
    }

    private static byte[] Hash(ReadOnlySpan<byte> token, byte[] pepper)
        => HMACSHA256.HashData(pepper, token);
}

internal sealed class TokenHashOptions
{
    internal TokenHashOptions(TokenPepper current, TokenPepper? previous)
    {
        Current = current;
        Previous = previous;
    }

    internal TokenPepper Current { get; }

    internal TokenPepper? Previous { get; }

    internal static TokenHashOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        TokenPepper current = ReadPepper(configuration, "Current", required: true)!;
        TokenPepper? previous = ReadPepper(configuration, "Previous", required: false);
        if (previous is not null && previous.Version == current.Version)
        {
            throw new InvalidOperationException("Current and previous token pepper versions must differ.");
        }

        return new TokenHashOptions(current, previous);
    }

    private static TokenPepper? ReadPepper(
        IConfiguration configuration,
        string name,
        bool required)
    {
        string versionKey = $"Auth:TokenHash:{name}PepperVersion";
        string secretKey = $"Auth:TokenHash:{name}Pepper";
        string? versionText = configuration[versionKey];
        string? secretText = configuration[secretKey];
        if (!required && string.IsNullOrWhiteSpace(versionText) && string.IsNullOrWhiteSpace(secretText))
        {
            return null;
        }

        if (!short.TryParse(
                versionText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out short version)
            || version <= 0
            || string.IsNullOrWhiteSpace(secretText))
        {
            throw new InvalidOperationException($"{name} token pepper configuration is invalid.");
        }

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(secretText);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{name} token pepper configuration is invalid.",
                exception);
        }

        if (secret.Length < 32)
        {
            throw new InvalidOperationException($"{name} token pepper must contain at least 256 bits.");
        }

        return new TokenPepper(version, secret);
    }
}

internal sealed class TokenPepper
{
    internal TokenPepper(short version, byte[] secret)
    {
        Version = version;
        Secret = secret;
    }

    internal short Version { get; }

    internal byte[] Secret { get; }
}
#pragma warning restore MA0048
