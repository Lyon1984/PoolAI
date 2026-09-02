using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace PoolAI.Modules.Gateway.Application;

internal sealed record GatewayOutboundTransportOptions(
    TimeSpan ConnectTimeout,
    TimeSpan FirstByteTimeout,
    TimeSpan StreamIdleTimeout,
    int MaxConnectionsPerServer,
    bool AllowLoopbackHttp,
    IReadOnlyList<GatewayPrivateEgressRule> PrivateEgressRules)
{
    private const string PrivateEgressRulesKey =
        "Supply:Health:PrivateEgressRules";
    private const int MaximumPrivateEgressRules = 64;

    internal GatewayOutboundTransportOptions(
        TimeSpan connectTimeout,
        bool allowLoopbackHttp)
        : this(
            connectTimeout,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
            256,
            allowLoopbackHttp,
            Array.Empty<GatewayPrivateEgressRule>())
    {
    }

    internal GatewayOutboundTransportOptions(
        TimeSpan connectTimeout,
        bool allowLoopbackHttp,
        IReadOnlyList<GatewayPrivateEgressRule> privateEgressRules)
        : this(
            connectTimeout,
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
            256,
            allowLoopbackHttp,
            privateEgressRules)
    {
    }

    internal static GatewayOutboundTransportOptions FromConfiguration(
        IConfiguration configuration,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        int connectTimeoutSeconds = configuration.GetValue(
            "Gateway:ConnectTimeoutSeconds",
            10);
        if (connectTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "The Gateway connect timeout is invalid.");
        }

        int firstByteTimeoutSeconds = configuration.GetValue(
            "Gateway:FirstByteTimeoutSeconds",
            60);
        if (firstByteTimeoutSeconds is < 5 or > 300)
        {
            throw new InvalidOperationException(
                "The Gateway first-byte timeout is invalid.");
        }

        int streamIdleTimeoutSeconds = configuration.GetValue(
            "Gateway:StreamIdleTimeoutSeconds",
            120);
        if (streamIdleTimeoutSeconds is < 15 or > 600)
        {
            throw new InvalidOperationException(
                "The Gateway stream-idle timeout is invalid.");
        }

        int maxConnectionsPerServer = configuration.GetValue(
            "Gateway:MaxConnectionsPerServer",
            256);
        if (maxConnectionsPerServer is < 16 or > 4_096)
        {
            throw new InvalidOperationException(
                "The Gateway upstream connection limit is invalid.");
        }

        return new(
            TimeSpan.FromSeconds(connectTimeoutSeconds),
            TimeSpan.FromSeconds(firstByteTimeoutSeconds),
            TimeSpan.FromSeconds(streamIdleTimeoutSeconds),
            maxConnectionsPerServer,
            IsDevelopmentOrTest(environmentName),
            ReadPrivateEgressRules(configuration));
    }

    private static bool IsDevelopmentOrTest(string environmentName) =>
        string.Equals(
            environmentName,
            "Development",
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            environmentName,
            "Test",
            StringComparison.OrdinalIgnoreCase);

    private static ReadOnlyCollection<GatewayPrivateEgressRule>
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

        SortedDictionary<int, GatewayPrivateEgressRule> parsed = [];
        HashSet<string> canonicalRules = new(StringComparer.Ordinal);
        foreach (IConfigurationSection entry in entries)
        {
            if (!TryParseCanonicalIndex(entry.Key, out int index)
                || entry.Value is null
                || entry.GetChildren().Any()
                || !GatewayPrivateEgressRule.TryParse(entry.Value, out var rule)
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

    private static bool TryParseCanonicalIndex(string value, out int index)
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
        new("The Gateway private egress rules are invalid.");
}
