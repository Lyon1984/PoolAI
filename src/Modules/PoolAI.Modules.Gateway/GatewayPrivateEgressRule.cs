using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PoolAI.Modules.Gateway.Application;

internal sealed record GatewayPrivateEgressRule(
    string CanonicalAuthority,
    string Host,
    int Port,
    GatewayIpCidr Network)
{
    internal string CanonicalKey => string.Create(
        CultureInfo.InvariantCulture,
        $"{CanonicalAuthority}|{Network.CanonicalValue}");

    internal bool Matches(Uri requestUri, IPAddress address) =>
        string.Equals(
            requestUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.Ordinal)
        && requestUri.Port == Port
        && string.Equals(NormalizeHost(requestUri), Host, StringComparison.Ordinal)
        && Network.Contains(GatewayIpCidr.NormalizeMappedAddress(address));

    internal static bool TryParse(
        string value,
        out GatewayPrivateEgressRule rule)
    {
        rule = null!;
        if (value.Length is < 1 or > 512
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character =>
                !char.IsAscii(character)
                || char.IsWhiteSpace(character)
                || character is '\\' or '\0'))
        {
            return false;
        }

        int separator = value.IndexOf('|');
        if (separator <= 0
            || separator != value.LastIndexOf('|')
            || separator == value.Length - 1
            || !TryParseAuthority(
                value[..separator],
                out string canonicalAuthority,
                out string host,
                out int port)
            || !GatewayIpCidr.TryParseCanonical(
                value[(separator + 1)..],
                allowZeroPrefix: false,
                out GatewayIpCidr? network)
            || !IsPrivateNetwork(network!))
        {
            return false;
        }

        rule = new(canonicalAuthority, host, port, network!);
        return true;
    }

    private static bool TryParseAuthority(
        string value,
        out string canonicalAuthority,
        out string host,
        out int port)
    {
        canonicalAuthority = string.Empty;
        host = string.Empty;
        port = 0;
        if (!value.StartsWith("https://", StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            || uri.HostNameType is UriHostNameType.Unknown
                or UriHostNameType.Basic
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        host = NormalizeHost(uri);
        if (host.Length == 0
            || string.Equals(host, "localhost", StringComparison.Ordinal)
            || IPAddress.TryParse(host, out IPAddress? literal)
                && IPAddress.IsLoopback(
                    GatewayIpCidr.NormalizeMappedAddress(literal)))
        {
            host = string.Empty;
            return false;
        }

        port = uri.Port;
        string authorityHost = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{host}]"
            : host;
        canonicalAuthority = string.Create(
            CultureInfo.InvariantCulture,
            $"https://{authorityHost}:{port}");
        return true;
    }

    private static bool IsPrivateNetwork(GatewayIpCidr network)
    {
        int separator = network.CanonicalValue.LastIndexOf('/');
        if (separator <= 0
            || !int.TryParse(
                network.CanonicalValue.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int prefixLength)
            || !IPAddress.TryParse(
                network.CanonicalValue.AsSpan(0, separator),
                out IPAddress? address))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 && prefixLength >= 8
                || bytes is [172, >= 16 and <= 31, _, _]
                    && prefixLength >= 12
                || bytes is [192, 168, _, _]
                    && prefixLength >= 16;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xfe) == 0xfc
            && prefixLength >= 7;
    }

    private static string NormalizeHost(Uri uri)
    {
        string host = uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost
            : uri.DnsSafeHost;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host.ToLowerInvariant();
    }
}
