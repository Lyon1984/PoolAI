using Microsoft.Extensions.Configuration;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PoolAI.Modules.Supply.Infrastructure.Health;

internal sealed record AccountHealthProbeHttpOptions(
    TimeSpan Timeout,
    int MaximumResponseBytes,
    bool AllowLoopbackHttp,
    IReadOnlyList<AccountHealthProbeHttpOptions.UpstreamPrivateEgressRule>
        PrivateEgressRules)
{
    private const string PrivateEgressRulesKey =
        "Supply:Health:PrivateEgressRules";
    private const int MaximumPrivateEgressRules = 64;

    internal AccountHealthProbeHttpOptions(
        TimeSpan Timeout,
        int MaximumResponseBytes,
        bool AllowLoopbackHttp)
        : this(
            Timeout,
            MaximumResponseBytes,
            AllowLoopbackHttp,
            Array.Empty<UpstreamPrivateEgressRule>())
    {
    }

    internal static AccountHealthProbeHttpOptions FromConfiguration(
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        int timeoutSeconds = configuration.GetValue(
            "Supply:Health:ProbeTimeoutSeconds",
            10);
        int maximumResponseBytes = configuration.GetValue(
            "Supply:Health:ProbeMaxResponseBytes",
            1_048_576);
        if (timeoutSeconds != 10)
        {
            throw new InvalidOperationException(
                "Supply health probe timeout must equal ten seconds.");
        }

        if (maximumResponseBytes != 1_048_576)
        {
            throw new InvalidOperationException(
                "Supply health probe response limit must equal one mebibyte.");
        }

        return new(
            TimeSpan.FromSeconds(timeoutSeconds),
            maximumResponseBytes,
            string.Equals(
                environmentName,
                "Development",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                environmentName,
                "Test",
                StringComparison.OrdinalIgnoreCase),
            ReadPrivateEgressRules(configuration));
    }

    private static ReadOnlyCollection<UpstreamPrivateEgressRule>
        ReadPrivateEgressRules(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection(
            PrivateEgressRulesKey);
        IConfigurationSection[] entries = [.. section.GetChildren()];
        if (section.Value is not null
            || entries.Length > MaximumPrivateEgressRules)
        {
            throw InvalidPrivateEgressRules();
        }

        SortedDictionary<int, UpstreamPrivateEgressRule> parsed = [];
        HashSet<string> canonicalRules = new(StringComparer.Ordinal);
        foreach (IConfigurationSection entry in entries)
        {
            if (!TryParseCanonicalIndex(entry.Key, out int index)
                || entry.Value is null
                || entry.GetChildren().Any()
                || !UpstreamPrivateEgressRule.TryParse(
                    entry.Value,
                    out UpstreamPrivateEgressRule rule)
                || !parsed.TryAdd(index, rule)
                || !canonicalRules.Add(rule.CanonicalKey))
            {
                throw InvalidPrivateEgressRules();
            }
        }

        if (!parsed.Keys.SequenceEqual(Enumerable.Range(0, parsed.Count)))
        {
            throw InvalidPrivateEgressRules();
        }

        return Array.AsReadOnly([.. parsed.Values]);
    }

    private static bool TryParseCanonicalIndex(
        string value,
        out int index)
    {
        index = -1;
        return value.Length is >= 1 and <= 2
            && (value.Length == 1 || value[0] != '0')
            && value.All(static character => character is >= '0' and <= '9')
            && int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index)
            && index is >= 0 and < MaximumPrivateEgressRules;
    }

    private static InvalidOperationException InvalidPrivateEgressRules() =>
        new(
            "Supply health private egress rules are invalid.");

    internal sealed record UpstreamPrivateEgressRule(
        string CanonicalAuthority,
        string Host,
        int Port,
        IPAddress Network,
        int PrefixLength)
    {
    internal string CanonicalKey =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{CanonicalAuthority}|{Network}/{PrefixLength}");

    internal bool Matches(Uri requestUri, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        ArgumentNullException.ThrowIfNull(address);
        IPAddress canonicalAddress = CanonicalAddress(address);
        return string.Equals(
                requestUri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            && requestUri.Port == Port
            && string.Equals(
                NormalizeHost(requestUri),
                Host,
                StringComparison.Ordinal)
            && Contains(Network, PrefixLength, canonicalAddress);
    }

    internal static bool TryParse(
        string value,
        out UpstreamPrivateEgressRule rule)
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
            || separator == value.Length - 1)
        {
            return false;
        }

        string authorityText = value[..separator];
        string cidrText = value[(separator + 1)..];
        if (!TryParseAuthority(
                authorityText,
                out string canonicalAuthority,
                out string host,
                out int port)
            || !TryParsePrivateNetwork(
                cidrText,
                out IPAddress network,
                out int prefixLength))
        {
            return false;
        }

        rule = new(
            canonicalAuthority,
            host,
            port,
            network,
            prefixLength);
        return true;
    }

    internal static IPAddress CanonicalAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;

    internal static bool IsPrivateAddress(IPAddress address)
    {
        IPAddress canonical = CanonicalAddress(address);
        byte[] bytes = canonical.GetAddressBytes();
        if (canonical.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes is [172, >= 16 and <= 31, _, _]
                || bytes is [192, 168, _, _];
        }

        return canonical.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xfe) == 0xfc;
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
                && IPAddress.IsLoopback(CanonicalAddress(literal)))
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

    private static string NormalizeHost(Uri uri)
    {
        string host = uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost
            : uri.DnsSafeHost;
        if (host.Length >= 2
            && host[0] == '['
            && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host.ToLowerInvariant();
    }

    private static bool TryParsePrivateNetwork(
        string value,
        out IPAddress network,
        out int prefixLength)
    {
        network = null!;
        prefixLength = -1;
        int separator = value.LastIndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(0, separator).Contains('/')
            || !TryParseCanonicalDecimal(
                value[(separator + 1)..],
                out prefixLength))
        {
            return false;
        }

        string addressText = value[..separator];
        if (!TryParseAddress(
                addressText,
                prefixLength,
                out IPAddress parsed))
        {
            return false;
        }

        byte[] networkBytes = parsed.GetAddressBytes();
        ClearHostBits(networkBytes, prefixLength);
        network = new IPAddress(networkBytes);
        string canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{network.ToString().ToLowerInvariant()}/{prefixLength}");
        return string.Equals(value, canonical, StringComparison.Ordinal)
            && IsPrivateNetwork(network, prefixLength);
    }

    private static bool TryParseAddress(
        string value,
        int prefixLength,
        out IPAddress address)
    {
        address = null!;
        if (value.Contains(':', StringComparison.Ordinal))
        {
            if (value.Contains('%', StringComparison.Ordinal)
                || !IPAddress.TryParse(value, out IPAddress? parsed)
                || parsed.AddressFamily != AddressFamily.InterNetworkV6
                || parsed.IsIPv4MappedToIPv6
                || prefixLength > 128)
            {
                return false;
            }

            address = parsed;
            return true;
        }

        string[] segments = value.Split('.');
        if (segments.Length != 4 || prefixLength > 32)
        {
            return false;
        }

        byte[] bytes = new byte[4];
        for (int index = 0; index < segments.Length; index++)
        {
            if (!TryParseCanonicalDecimal(segments[index], out int parsed)
                || parsed > byte.MaxValue)
            {
                return false;
            }

            bytes[index] = checked((byte)parsed);
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool TryParseCanonicalDecimal(
        string value,
        out int parsed)
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

    private static bool IsPrivateNetwork(
        IPAddress network,
        int prefixLength)
    {
        if (network.AddressFamily == AddressFamily.InterNetwork)
        {
            return prefixLength >= 8
                    && Contains(
                        IPAddress.Parse("10.0.0.0"),
                        8,
                        network)
                || prefixLength >= 12
                    && Contains(
                        IPAddress.Parse("172.16.0.0"),
                        12,
                        network)
                || prefixLength >= 16
                    && Contains(
                        IPAddress.Parse("192.168.0.0"),
                        16,
                        network);
        }

        return prefixLength >= 7
            && Contains(
                IPAddress.Parse("fc00::"),
                7,
                network);
    }

    private static bool Contains(
        IPAddress network,
        int prefixLength,
        IPAddress address)
    {
        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        ReadOnlySpan<byte> networkBytes = network.GetAddressBytes();
        ReadOnlySpan<byte> addressBytes = address.GetAddressBytes();
        int wholeBytes = prefixLength / 8;
        if (!networkBytes[..wholeBytes].SequenceEqual(
                addressBytes[..wholeBytes]))
        {
            return false;
        }

        int remainingBits = prefixLength % 8;
        if (remainingBits == 0)
        {
            return true;
        }

        int mask = 0xff << (8 - remainingBits);
        return (networkBytes[wholeBytes] & mask)
            == (addressBytes[wholeBytes] & mask);
    }

    private static void ClearHostBits(
        Span<byte> address,
        int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            byte mask = checked(
                (byte)(256 - (1 << (8 - remainingBits))));
            address[wholeBytes] &= mask;
            wholeBytes++;
        }

        address[wholeBytes..].Clear();
    }
}
}
