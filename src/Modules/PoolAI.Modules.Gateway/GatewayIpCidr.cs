using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayIpCidr
{
    private readonly byte[] _networkBytes;
    private readonly AddressFamily _addressFamily;
    private readonly int _prefixLength;

    private GatewayIpCidr(
        IPAddress network,
        int prefixLength,
        string canonicalValue)
    {
        _networkBytes = network.GetAddressBytes();
        _addressFamily = network.AddressFamily;
        _prefixLength = prefixLength;
        CanonicalValue = canonicalValue;
    }

    public string CanonicalValue { get; }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        IPAddress candidate = NormalizeMappedAddress(address);
        if (candidate.AddressFamily != _addressFamily)
        {
            return false;
        }

        ReadOnlySpan<byte> candidateBytes = candidate.GetAddressBytes();
        int completeBytes = _prefixLength / 8;
        if (!candidateBytes[..completeBytes].SequenceEqual(
                _networkBytes.AsSpan(0, completeBytes)))
        {
            return false;
        }

        int remainingBits = _prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        byte mask = unchecked((byte)(0xff << (8 - remainingBits)));
        return (candidateBytes[completeBytes] & mask)
            == (_networkBytes[completeBytes] & mask);
    }

    public static bool TryParseCanonical(
        string? value,
        bool allowZeroPrefix,
        out GatewayIpCidr? cidr)
    {
        cidr = null;
        if (string.IsNullOrEmpty(value)
            || value.Length > 64
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        int separator = value.LastIndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(0, separator).Contains('/')
            || !TryParseCanonicalDecimal(
                value.AsSpan(separator + 1),
                out int prefixLength)
            || !allowZeroPrefix && prefixLength == 0
            || !TryParseCanonicalAddress(
                value.AsSpan(0, separator),
                allowMappedIpv6: false,
                out IPAddress? address))
        {
            return false;
        }

        IPAddress parsedAddress = address!;
        int maximumPrefix = parsedAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => -1,
        };
        if (prefixLength > maximumPrefix)
        {
            return false;
        }

        byte[] networkBytes = parsedAddress.GetAddressBytes();
        ClearHostBits(networkBytes, prefixLength);
        IPAddress network = new(networkBytes);
        string canonicalValue = string.Create(
            CultureInfo.InvariantCulture,
            $"{network.ToString().ToLowerInvariant()}/{prefixLength}");
        if (!string.Equals(value, canonicalValue, StringComparison.Ordinal))
        {
            return false;
        }

        cidr = new GatewayIpCidr(
            network,
            prefixLength,
            canonicalValue);
        return true;
    }

    internal static bool TryParseCanonicalAddress(
        ReadOnlySpan<char> value,
        bool allowMappedIpv6,
        out IPAddress? address)
    {
        address = null;
        if (value.IsEmpty
            || value.Contains('%')
            || value.Contains('/')
            || ContainsWhitespace(value))
        {
            return false;
        }

        string text = value.ToString();
        if (!text.Contains(':', StringComparison.Ordinal))
        {
            return TryParseCanonicalIpv4(value, out address);
        }

        int dottedTailSeparator = text.LastIndexOf(':');
        if (text.Contains('.', StringComparison.Ordinal)
            && (dottedTailSeparator < 0
                || !TryParseCanonicalIpv4(
                    value[(dottedTailSeparator + 1)..],
                    out _)))
        {
            return false;
        }

        if (!IPAddress.TryParse(text, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetworkV6
            || parsed.IsIPv4MappedToIPv6 && !allowMappedIpv6
            || !string.Equals(
                text,
                parsed.ToString().ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return false;
        }

        address = NormalizeMappedAddress(parsed);
        return true;
    }

    internal static IPAddress NormalizeMappedAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;
    }

    public override string ToString() => nameof(GatewayIpCidr);

    private static bool TryParseCanonicalIpv4(
        ReadOnlySpan<char> value,
        out IPAddress? address)
    {
        address = null;
        Span<byte> bytes = stackalloc byte[4];
        int segmentIndex = 0;
        int segmentStart = 0;
        for (int index = 0; index <= value.Length; index++)
        {
            if (index != value.Length && value[index] != '.')
            {
                continue;
            }

            if (segmentIndex >= bytes.Length
                || !TryParseCanonicalDecimal(
                    value[segmentStart..index],
                    out int segment)
                || segment > byte.MaxValue)
            {
                return false;
            }

            bytes[segmentIndex++] = checked((byte)segment);
            segmentStart = index + 1;
        }

        if (segmentIndex != bytes.Length)
        {
            return false;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool TryParseCanonicalDecimal(
        ReadOnlySpan<char> value,
        out int parsed)
    {
        parsed = 0;
        if (value.IsEmpty
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

    private static bool ContainsWhitespace(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                return true;
            }
        }

        return false;
    }

    private static void ClearHostBits(
        Span<byte> address,
        int prefixLength)
    {
        int completeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            address[completeBytes] &= unchecked(
                (byte)(0xff << (8 - remainingBits)));
            completeBytes++;
        }

        address[completeBytes..].Clear();
    }
}
