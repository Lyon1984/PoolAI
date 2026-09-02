using System.Collections.ObjectModel;
using System.Net;

namespace PoolAI.Modules.Gateway.Application;

public sealed class GatewayIngressOptions
{
    public const int DefaultForwardedForLimit = 1;
    public const int MaximumForwardedForLimit = 8;
    public const int MaximumForwardedForUtf8Bytes = 1_024;
    public const int MaximumTrustedProxyCidrs = 64;

    private readonly ReadOnlyCollection<GatewayIpCidr> _trustedProxyNetworks;

    public GatewayIngressOptions(
        IReadOnlyList<string>? trustedProxyCidrs = null,
        int forwardedForLimit = DefaultForwardedForLimit)
    {
        trustedProxyCidrs ??= [];
        if (trustedProxyCidrs.Count > MaximumTrustedProxyCidrs)
        {
            throw new ArgumentException(
                "At most 64 trusted proxy CIDRs are allowed.",
                nameof(trustedProxyCidrs));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(
            forwardedForLimit,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            forwardedForLimit,
            MaximumForwardedForLimit);

        List<string> canonicalValues = new(trustedProxyCidrs.Count);
        List<GatewayIpCidr> networks = new(trustedProxyCidrs.Count);
        HashSet<string> uniqueValues = new(StringComparer.Ordinal);
        foreach (string value in trustedProxyCidrs)
        {
            if (!GatewayIpCidr.TryParseCanonical(
                    value,
                    allowZeroPrefix: false,
                    out GatewayIpCidr? network)
                || !uniqueValues.Add(network!.CanonicalValue))
            {
                throw new ArgumentException(
                    "Trusted proxy CIDRs must be unique canonical non-zero networks.",
                    nameof(trustedProxyCidrs));
            }

            GatewayIpCidr parsedNetwork = network!;
            canonicalValues.Add(parsedNetwork.CanonicalValue);
            networks.Add(parsedNetwork);
        }

        TrustedProxyCidrs = canonicalValues.AsReadOnly();
        _trustedProxyNetworks = networks.AsReadOnly();
        ForwardedForLimit = forwardedForLimit;
    }

    public IReadOnlyList<string> TrustedProxyCidrs { get; }

    public int ForwardedForLimit { get; }

    internal bool IsTrusted(IPAddress address) =>
        _trustedProxyNetworks.Any(network => network.Contains(address));

    public override string ToString() => nameof(GatewayIngressOptions);
}
