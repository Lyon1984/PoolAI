using System.Net;
using System.Net.Sockets;

namespace PoolAI.Modules.Gateway.Application;

internal static class GatewayUpstreamAddressClassifier
{
    internal static bool AreAllAllowed(
        Uri requestUri,
        IReadOnlyList<IPAddress> addresses,
        GatewayOutboundTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(options);
        return addresses.Count != 0
            && addresses.All(address => IsAllowed(requestUri, address, options));
    }

    private static bool IsAllowed(
        Uri requestUri,
        IPAddress address,
        GatewayOutboundTransportOptions options)
    {
        IPAddress canonical = GatewayIpCidr.NormalizeMappedAddress(address);
        bool loopbackHttp = string.Equals(
                requestUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.Ordinal)
            && IsExactLoopbackHost(requestUri);
        if (loopbackHttp && options.AllowLoopbackHttp)
        {
            return IPAddress.IsLoopback(canonical);
        }

        if (!string.Equals(
                requestUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            || IsAlwaysForbidden(canonical))
        {
            return false;
        }

        if (IsPrivateAddress(canonical))
        {
            return options.PrivateEgressRules.Any(rule =>
                rule.Matches(requestUri, canonical));
        }

        return !IsReservedIpv4(canonical) && !IsReservedIpv6(canonical);
    }

    private static bool IsExactLoopbackHost(Uri requestUri)
    {
        string host = requestUri.HostNameType == UriHostNameType.Dns
            ? requestUri.IdnHost
            : requestUri.DnsSafeHost;
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return string.Equals(host, "localhost", StringComparison.Ordinal)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal);
    }

    private static bool IsAlwaysForbidden(IPAddress address) =>
        IPAddress.IsLoopback(address)
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.IPv6Any)
        || IsLinkLocal(address)
        || IsMulticast(address);

    private static bool IsLinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes() is [169, 254, _, _]
            : address.IsIPv6LinkLocal;

    private static bool IsMulticast(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] is >= 224 and <= 239
            : bytes[0] == 0xff;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes is [172, >= 16 and <= 31, _, _]
                || bytes is [192, 168, _, _];
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xfe) == 0xfc;
    }

    private static bool IsReservedIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 0
            || bytes is [100, >= 64 and <= 127, _, _]
            || bytes is [192, 0, 0, _]
            || bytes is [192, 0, 2, _]
            || bytes is [192, 88, 99, _]
            || bytes is [198, 18 or 19, _, _]
            || bytes is [198, 51, 100, _]
            || bytes is [203, 0, 113, _]
            || bytes[0] >= 240;
    }

    private static bool IsReservedIpv6(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        return address.IsIPv6SiteLocal
            || bytes.AsSpan(0, 12).SequenceEqual(new byte[12])
            || bytes is [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, ..]
            || bytes is [0x20, 0x01, 0x00, 0x02, _, _, _, _, ..]
            || bytes is [0x20, 0x01, 0x0d, 0xb8, ..];
    }
}
