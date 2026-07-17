namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static string Encode(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("The value must not be empty.", nameof(value));
        }

        int encodedLength = checked(((value.Length * 8) + 4) / 5);
        return string.Create(encodedLength, value.ToArray(), static (destination, bytes) =>
        {
            uint buffer = 0;
            int bufferedBits = 0;
            int destinationIndex = 0;
            foreach (byte item in bytes)
            {
                buffer = (buffer << 8) | item;
                bufferedBits += 8;
                while (bufferedBits >= 5)
                {
                    bufferedBits -= 5;
                    destination[destinationIndex++] = Alphabet[(int)((buffer >> bufferedBits) & 31)];
                }

                buffer = bufferedBits == 0
                    ? 0
                    : buffer & ((1u << bufferedBits) - 1);
            }

            if (bufferedBits > 0)
            {
                destination[destinationIndex] = Alphabet[(int)((buffer << (5 - bufferedBits)) & 31)];
            }

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        });
    }

    internal static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            throw new FormatException("Base32 value is required.");
        }

        byte[] decoded = new byte[checked((value.Length * 5) / 8)];
        try
        {
            uint buffer = 0;
            int bufferedBits = 0;
            int decodedIndex = 0;
            foreach (char character in value)
            {
                int digit = DecodeDigit(character);
                if (digit < 0)
                {
                    throw new FormatException(
                        "The value is not canonical uppercase unpadded base32.");
                }

                buffer = (buffer << 5) | (uint)digit;
                bufferedBits += 5;
                if (bufferedBits >= 8)
                {
                    bufferedBits -= 8;
                    decoded[decodedIndex++] = (byte)(buffer >> bufferedBits);
                }

                buffer = bufferedBits == 0
                    ? 0
                    : buffer & ((1u << bufferedBits) - 1);
            }

            if (buffer != 0 || !string.Equals(Encode(decoded), value, StringComparison.Ordinal))
            {
                throw new FormatException(
                    "The value is not canonical uppercase unpadded base32.");
            }

            return decoded;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            throw;
        }
    }

    private static int DecodeDigit(char value) => value switch
    {
        >= 'A' and <= 'Z' => value - 'A',
        >= '2' and <= '7' => value - '2' + 26,
        _ => -1,
    };
}
