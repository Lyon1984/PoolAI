using System.Buffers.Binary;
using System.Security.Cryptography;
using PoolAI.Modules.Identity.Application.Ports;

namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal sealed class VersionedPasswordHasher : IVersionedPasswordHasher
{
    internal const string Prefix = "poolai-password-v1:";
    internal const int IterationCount = 100_000;
    private const int SaltSize = 16;
    private const int SubkeySize = 32;
    private const int HeaderSize = 13;
    private const uint HmacSha512Prf = 2;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            IterationCount,
            HashAlgorithmName.SHA512,
            SubkeySize);
        byte[] output = new byte[HeaderSize + SaltSize + SubkeySize];
        output[0] = 0x01;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(1, 4), HmacSha512Prf);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(5, 4), IterationCount);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(9, 4), SaltSize);
        salt.CopyTo(output, HeaderSize);
        subkey.CopyTo(output, HeaderSize + SaltSize);
        CryptographicOperations.ZeroMemory(subkey);
        return Prefix + Convert.ToBase64String(output);
    }

    public bool Verify(string encodedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        ArgumentNullException.ThrowIfNull(password);
        if (!encodedHash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(encodedHash[Prefix.Length..]);
            if (decoded.Length != HeaderSize + SaltSize + SubkeySize || decoded[0] != 0x01)
            {
                return false;
            }

            uint prf = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(1, 4));
            uint iterations = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(5, 4));
            uint saltLength = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(9, 4));
            if (prf != HmacSha512Prf
                || iterations != IterationCount
                || saltLength != SaltSize)
            {
                return false;
            }

            ReadOnlySpan<byte> salt = decoded.AsSpan(HeaderSize, SaltSize);
            ReadOnlySpan<byte> expected = decoded.AsSpan(HeaderSize + SaltSize, SubkeySize);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                (int)iterations,
                HashAlgorithmName.SHA512,
                expected.Length);
            bool verified = CryptographicOperations.FixedTimeEquals(actual, expected);
            CryptographicOperations.ZeroMemory(actual);
            return verified;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
