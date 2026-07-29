#pragma warning disable MA0051 // The frozen URL policy is kept visible as one validation routine.
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PoolAI.Modules.Supply.Domain;

internal static partial class AccountInput
{
    internal static string Name(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "An Account name must contain between 1 and 100 non-control characters.",
                nameof(value));
        }

        return normalized;
    }

    internal static string BaseUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryCountUnicodeScalars(value, out int scalarCount)
            || scalarCount is < 1 or > 2048
            || !BaseUrlPattern().IsMatch(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Port is < 1 or > 65535
            || !HasValidOriginalHost(value))
        {
            throw new ArgumentException(
                "The Account Base URL is invalid.",
                nameof(value));
        }

        return value;
    }

    internal static string Credential(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 16 or > 4096)
        {
            throw new ArgumentException(
                "An Account credential must contain between 16 and 4096 characters.",
                nameof(value));
        }

        return value;
    }

    internal static string CredentialPrefix(string credential)
    {
        _ = Credential(credential);
        byte[] bytes = Encoding.UTF8.GetBytes(credential);
        try
        {
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            _ = SHA256.HashData(bytes, digest);
            return $"sha256:{Convert.ToHexStringLower(digest[..6])}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static string IdempotencyKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128
            || value.Any(static character => character is < '\x21' or > '\x7e'))
        {
            throw new ArgumentException(
                "An idempotency key must contain 1 to 128 visible ASCII characters.",
                nameof(value));
        }

        return value;
    }

    internal static string Reason(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 500
            || normalized.Any(static character => character is '\r' or '\n'))
        {
            throw new ArgumentException(
                "A reason must contain between 1 and 500 characters.",
                nameof(value));
        }

        return normalized;
    }

    internal static int MaxConcurrency(int value)
    {
        if (value is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    internal static int Priority(int value)
    {
        if (value is < -100000 or > 100000)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    internal static int Weight(int value)
    {
        if (value is < 1 or > 100000)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }

    internal static void ExpectedVersion(long value) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

    private static bool HasValidOriginalHost(string value)
    {
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        int authorityEnd = value.IndexOf('/', authorityStart);
        ReadOnlySpan<char> authority = authorityEnd < 0
            ? value.AsSpan(authorityStart)
            : value.AsSpan(authorityStart, authorityEnd - authorityStart);
        if (authority.IsEmpty)
        {
            return false;
        }

        if (authority[0] == '[')
        {
            int closingBracket = authority.IndexOf(']');
            if (closingBracket <= 1)
            {
                return false;
            }

            ReadOnlySpan<char> suffix = authority[(closingBracket + 1)..];
            if (!suffix.IsEmpty && suffix[0] != ':')
            {
                return false;
            }

            return IPAddress.TryParse(
                    authority[1..closingBracket],
                    out IPAddress? address)
                && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        int portSeparator = authority.LastIndexOf(':');
        ReadOnlySpan<char> host = portSeparator < 0
            ? authority
            : authority[..portSeparator];
        if (host.IsEmpty)
        {
            return false;
        }

        bool numericAddressCandidate = true;
        foreach (char character in host)
        {
            if (character is not (>= '0' and <= '9') and not '.')
            {
                numericAddressCandidate = false;
                break;
            }
        }

        if (numericAddressCandidate)
        {
            return IPAddress.TryParse(host, out IPAddress? address)
                && address.AddressFamily == AddressFamily.InterNetwork
                && host.SequenceEqual(address.ToString());
        }

        if (host.Length > 253)
        {
            return false;
        }

        foreach (Range labelRange in host.Split('.'))
        {
            ReadOnlySpan<char> label = host[labelRange];
            if (label.Length is < 1 or > 63
                || !IsAsciiLetterOrDigit(label[0])
                || !IsAsciiLetterOrDigit(label[^1]))
            {
                return false;
            }

            foreach (char character in label[1..^1])
            {
                if (!IsAsciiLetterOrDigit(character) && character != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool TryCountUnicodeScalars(string value, out int count)
    {
        count = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(
                remaining,
                out _,
                out int consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            count++;
            remaining = remaining[consumed..];
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';

    [GeneratedRegex(
        @"\A(?:https://(?:[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])|http://(?:localhost|127\.0\.0\.1|\[::1\]))(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:/[^\s?#]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex BaseUrlPattern();
}
#pragma warning restore MA0051
