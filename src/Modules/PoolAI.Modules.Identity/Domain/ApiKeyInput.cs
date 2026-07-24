using System.Net;
using System.Net.Sockets;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Domain;

internal static class ApiKeyInput
{
    internal static string Name(string value)
    {
        if (!ApiKeyTextRules.IsValidName(value))
        {
            throw new ArgumentException("The API Key name is invalid.", nameof(value));
        }

        return value;
    }

    internal static IReadOnlyList<string> AllowedCidrs(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > 50)
        {
            throw new ArgumentException(
                "At most 50 API Key CIDR entries are allowed.",
                nameof(values));
        }

        SortedSet<string> canonical = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            canonical.Add(CanonicalCidr(value));
        }

        return canonical.ToArray();
    }

    internal static DateTimeOffset PostgresTimestamp(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        long normalizedTicks =
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond;
        return new DateTimeOffset(normalizedTicks, TimeSpan.Zero);
    }

    internal static string AdminReason(string value)
    {
        if (!ApiKeyTextRules.IsValidAdminReason(value))
        {
            throw new ArgumentException(
                "The API Key audit reason is invalid.",
                nameof(value));
        }

        return value;
    }

    internal static string CanonicalCidr(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('%', StringComparison.Ordinal))
        {
            throw new ArgumentException("The API Key CIDR is invalid.", nameof(value));
        }

        int separator = value.LastIndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(0, separator).Contains('/'))
        {
            throw new ArgumentException("The API Key CIDR is invalid.", nameof(value));
        }

        string addressText = value[..separator];
        string prefixText = value[(separator + 1)..];
        if (!TryParseCanonicalDecimal(prefixText, out int prefixLength))
        {
            throw new ArgumentException("The API Key CIDR prefix is invalid.", nameof(value));
        }

        IPAddress address = ParseAddress(addressText, prefixLength, value);
        byte[] networkBytes = address.GetAddressBytes();
        ClearHostBits(networkBytes, prefixLength);
        string network = new IPAddress(networkBytes)
            .ToString()
            .ToLowerInvariant();
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{network}/{prefixLength}");
    }

    private static IPAddress ParseAddress(
        string addressText,
        int prefixLength,
        string original)
    {
        if (addressText.Contains(':', StringComparison.Ordinal))
        {
            if (addressText.Contains('.', StringComparison.Ordinal))
            {
                int dottedTailSeparator = addressText.LastIndexOf(':');
                if (dottedTailSeparator < 0)
                {
                    throw new ArgumentException(
                        "The API Key IPv6 CIDR is invalid.",
                        nameof(original));
                }

                _ = ParseIpv4(
                    addressText[(dottedTailSeparator + 1)..],
                    original);
            }

            if (!IPAddress.TryParse(addressText, out IPAddress? address)
                || address.AddressFamily != AddressFamily.InterNetworkV6
                || address.IsIPv4MappedToIPv6
                || prefixLength > 128)
            {
                throw new ArgumentException(
                    "The API Key IPv6 CIDR is invalid.",
                    nameof(original));
            }

            return address;
        }

        IPAddress ipv4 = ParseIpv4(addressText, original);
        if (prefixLength > 32)
        {
            throw new ArgumentException(
                "The API Key IPv4 CIDR is invalid.",
                nameof(original));
        }

        return ipv4;
    }

    private static IPAddress ParseIpv4(string addressText, string original)
    {
        string[] segments = addressText.Split('.');
        if (segments.Length != 4)
        {
            throw new ArgumentException("The API Key IPv4 CIDR is invalid.", nameof(original));
        }

        byte[] address = new byte[4];
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (!TryParseCanonicalDecimal(segment, out int value) || value > byte.MaxValue)
            {
                throw new ArgumentException("The API Key IPv4 CIDR is invalid.", nameof(original));
            }

            address[index] = checked((byte)value);
        }

        return new IPAddress(address);
    }

    private static bool TryParseCanonicalDecimal(string value, out int parsed)
    {
        parsed = 0;
        if (value.Length == 0
            || value.Length > 3
            || value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            parsed = checked((parsed * 10) + (character - '0'));
        }

        return true;
    }

    private static void ClearHostBits(Span<byte> address, int prefixLength)
    {
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            address[fullBytes] &= checked((byte)(0xff << (8 - remainingBits)));
            fullBytes++;
        }

        address[fullBytes..].Clear();
    }
}
