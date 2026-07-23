namespace PoolAI.Modules.Identity.Infrastructure.Security;

internal static class Base64Url
{
    internal static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0
            || value.Contains('=', StringComparison.Ordinal)
            || value.Any(static character =>
                !(character is >= 'A' and <= 'Z'
                    or >= 'a' and <= 'z'
                    or >= '0' and <= '9'
                    or '-' or '_')))
        {
            throw new FormatException("The value is not canonical unpadded base64url.");
        }

        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("The base64url length is invalid."),
        };
        byte[] decoded = Convert.FromBase64String(base64);
        if (!string.Equals(Encode(decoded), value, StringComparison.Ordinal))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
            throw new FormatException("The value is not canonical unpadded base64url.");
        }

        return decoded;
    }
}
