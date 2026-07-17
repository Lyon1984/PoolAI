using System.Security.Cryptography;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class OneTimeChallengeHasher : IOneTimeChallengeHasher
{
    private const int ChallengeSize = 16;
    private const int HashSize = 32;
    private readonly TokenPepper _current;
    private readonly TokenPepper? _previous;

    internal OneTimeChallengeHasher(TokenHashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _current = options.Current;
        _previous = options.Previous;
    }

    public OneTimeChallengeSecret Create()
    {
        EntityId challenge = EntityId.New();
        byte[] challengeBytes = CanonicalBytes(challenge);
        try
        {
            return new OneTimeChallengeSecret(
                challenge,
                Hash(challengeBytes, _current.Secret),
                _current.Version);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeBytes);
        }
    }

    public IReadOnlyList<OneTimeChallengeCandidate> HashCandidates(EntityId challenge)
    {
        byte[] challengeBytes = CanonicalBytes(challenge);
        try
        {
            List<OneTimeChallengeCandidate> candidates = new(2)
            {
                new(Hash(challengeBytes, _current.Secret), _current.Version),
            };
            if (_previous is not null)
            {
                candidates.Add(new OneTimeChallengeCandidate(
                    Hash(challengeBytes, _previous.Secret),
                    _previous.Version));
            }

            return candidates;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeBytes);
        }
    }

    public bool Verify(EntityId challenge, byte[] expectedHash, short pepperVersion)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        TokenPepper? pepper = _current.Version == pepperVersion
            ? _current
            : _previous?.Version == pepperVersion
                ? _previous
                : null;
        if (pepper is null || expectedHash.Length != HashSize)
        {
            return false;
        }

        byte[] challengeBytes = CanonicalBytes(challenge);
        byte[] actualHash = Hash(challengeBytes, pepper.Secret);
        try
        {
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challengeBytes);
            CryptographicOperations.ZeroMemory(actualHash);
        }
    }

    private static byte[] CanonicalBytes(EntityId challenge)
    {
        byte[] bytes = new byte[ChallengeSize];
        if (!challenge.Value.TryWriteBytes(bytes, bigEndian: true, out int written)
            || written != ChallengeSize)
        {
            throw new InvalidOperationException("The challenge UUID could not be encoded.");
        }

        return bytes;
    }

    private static byte[] Hash(ReadOnlySpan<byte> challenge, byte[] pepper) =>
        HMACSHA256.HashData(pepper, challenge);
}
