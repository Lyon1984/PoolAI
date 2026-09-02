using System.Net;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

public sealed class GatewayTrustedProxyTests
{
    [Fact]
    public void IngressOptionsUseFailClosedDefaults()
    {
        GatewayIngressOptions options = new();

        Assert.Empty(options.TrustedProxyCidrs);
        Assert.Equal(1, options.ForwardedForLimit);
        Assert.Equal(1_024, GatewayIngressOptions.MaximumForwardedForUtf8Bytes);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.1/8")]
    [InlineData("2001:db8::1/64")]
    [InlineData("192.168.001.0/24")]
    [InlineData("2001:DB8::/64")]
    [InlineData("fe80::1%1/128")]
    [InlineData("::ffff:192.0.2.1/128")]
    public void IngressOptionsRejectNonCanonicalTrustedNetworks(string cidr)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            new GatewayIngressOptions([cidr]));

        Assert.DoesNotContain(cidr, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IngressOptionsRejectDuplicatesAndOutOfRangeLimits()
    {
        Assert.Throws<ArgumentException>(() =>
            new GatewayIngressOptions(["10.0.0.0/8", "10.0.0.0/8"]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayIngressOptions([], forwardedForLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayIngressOptions([], forwardedForLimit: 9));
    }

    [Fact]
    public void IngressOptionsRejectMoreThanSixtyFourNetworks()
    {
        string[] cidrs = Enumerable.Range(0, 65)
            .Select(static index => $"198.51.100.{index}/32")
            .ToArray();

        Assert.Throws<ArgumentException>(() =>
            new GatewayIngressOptions(cidrs));
    }

    [Fact]
    public void NullSocketPeerFailsClosed()
    {
        GatewayClientIpResolver resolver = Resolver();

        Assert.False(resolver.TryResolveClientAddress(
            null,
            ["192.0.2.10"],
            out IPAddress? resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void UntrustedPeerIgnoresEveryForwardingHeader()
    {
        GatewayClientIpResolver resolver = Resolver();
        IPAddress peer = IPAddress.Parse("198.51.100.8");
        string oversized = new string(' ', 1_024) + "192.0.2.10";

        Assert.True(resolver.TryResolveClientAddress(
            peer,
            [oversized, "malformed"],
            out IPAddress? resolved));
        Assert.Equal(peer, resolved);
    }

    [Fact]
    public void MappedSocketPeerIsNormalizedBeforeTrustAndReturn()
    {
        GatewayClientIpResolver resolver = new(new GatewayIngressOptions());

        Assert.True(resolver.TryResolveClientAddress(
            IPAddress.Parse("::ffff:192.0.2.10"),
            null,
            out IPAddress? resolved));
        Assert.Equal(IPAddress.Parse("192.0.2.10"), resolved);
    }

    [Fact]
    public void TrustedPeerRequiresExactlyOneBoundedLogicalField()
    {
        GatewayClientIpResolver resolver = Resolver();
        IPAddress peer = IPAddress.Parse("10.0.0.3");

        Assert.False(resolver.TryResolveClientAddress(
            peer,
            null,
            out _));
        Assert.False(resolver.TryResolveClientAddress(
            peer,
            [],
            out _));
        Assert.False(resolver.TryResolveClientAddress(
            peer,
            ["192.0.2.10", "198.51.100.10"],
            out _));
        Assert.False(resolver.TryResolveClientAddress(
            peer,
            [new string(' ', 1_024) + "192.0.2.10"],
            out _));
    }

    [Fact]
    public void TrustedChainIsPeeledFromRightToLeft()
    {
        GatewayClientIpResolver resolver = Resolver(forwardedForLimit: 3);

        Assert.True(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            ["203.0.113.7, 10.0.0.1, 10.0.0.2"],
            out IPAddress? resolved));
        Assert.Equal(IPAddress.Parse("203.0.113.7"), resolved);
    }

    [Fact]
    public void FirstUntrustedRightHandHopStopsPeelingAndIgnoresSpoofedLeftText()
    {
        GatewayClientIpResolver resolver = Resolver(forwardedForLimit: 2);

        Assert.True(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            ["203.0.113.7, 198.51.100.9"],
            out IPAddress? resolved));
        Assert.Equal(IPAddress.Parse("198.51.100.9"), resolved);
    }

    [Fact]
    public void AllTrustedHopsResolveToTheLeftmostHop()
    {
        GatewayClientIpResolver resolver = new(new GatewayIngressOptions(
            ["10.0.0.0/8", "192.168.0.0/16"],
            forwardedForLimit: 2));

        Assert.True(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            ["192.168.1.1, 10.0.0.2"],
            out IPAddress? resolved));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), resolved);
    }

    [Fact]
    public void OptionalWhitespaceAndCanonicalMappedIpv6AreNormalized()
    {
        GatewayClientIpResolver resolver = Resolver();

        Assert.True(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            [" \t::ffff:192.0.2.17\t "],
            out IPAddress? resolved));
        Assert.Equal(IPAddress.Parse("192.0.2.17"), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.0.2.1:443")]
    [InlineData("[2001:db8::1]")]
    [InlineData("\"192.0.2.1\"")]
    [InlineData("fe80::1%1")]
    [InlineData("unknown")]
    [InlineData("_hidden")]
    [InlineData("192.168.001.1")]
    [InlineData("2001:DB8::1")]
    [InlineData("192.0.2.1 198.51.100.1")]
    [InlineData("192.0.2.1,")]
    public void MalformedTrustedChainFailsClosed(string forwardedFor)
    {
        GatewayClientIpResolver resolver = Resolver(forwardedForLimit: 2);

        Assert.False(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            [forwardedFor],
            out IPAddress? resolved));
        Assert.Null(resolved);
    }

    [Fact]
    public void ForwardedHopCountCannotExceedConfiguredLimit()
    {
        GatewayClientIpResolver resolver = Resolver();

        Assert.False(resolver.TryResolveClientAddress(
            IPAddress.Parse("10.0.0.3"),
            ["192.0.2.1, 10.0.0.2"],
            out _));
    }

    [Fact]
    public void ApiKeyCidrsAreMatchedAfterResolutionAndMalformedListsFailClosed()
    {
        GatewayClientIpResolver resolver = Resolver();
        IPAddress peer = IPAddress.Parse("10.0.0.3");

        Assert.True(resolver.TryResolveAuthorizedClientAddress(
            peer,
            ["192.0.2.17"],
            [],
            out IPAddress? unrestricted));
        Assert.Equal(IPAddress.Parse("192.0.2.17"), unrestricted);

        Assert.True(resolver.TryResolveAuthorizedClientAddress(
            peer,
            ["192.0.2.17"],
            ["192.0.2.0/24"],
            out IPAddress? matched));
        Assert.Equal(IPAddress.Parse("192.0.2.17"), matched);

        Assert.False(resolver.TryResolveAuthorizedClientAddress(
            peer,
            ["192.0.2.17"],
            ["198.51.100.0/24"],
            out _));
        Assert.False(resolver.TryResolveAuthorizedClientAddress(
            peer,
            ["192.0.2.17"],
            ["192.0.2.0/24", "malformed"],
            out _));
    }

    [Fact]
    public void ApiKeyCidrMatchingNormalizesMappedClientsAndSupportsZeroPrefixes()
    {
        Assert.True(GatewayClientIpResolver.IsAllowedByApiKeyCidrs(
            IPAddress.Parse("::ffff:192.0.2.17"),
            ["0.0.0.0/0"]));
        Assert.True(GatewayClientIpResolver.IsAllowedByApiKeyCidrs(
            IPAddress.Parse("2001:db8::17"),
            ["::/0"]));
        Assert.False(GatewayClientIpResolver.IsAllowedByApiKeyCidrs(
            IPAddress.Parse("192.0.2.17"),
            null));
        Assert.False(GatewayClientIpResolver.IsAllowedByApiKeyCidrs(
            null,
            []));
    }

    [Fact]
    public void ResolverStringRepresentationsDoNotExposeAddressesOrHeaders()
    {
        GatewayIngressOptions options = new(["10.0.0.0/8"]);
        GatewayClientIpResolver resolver = new(options);

        Assert.Equal(nameof(GatewayIngressOptions), options.ToString());
        Assert.Equal(nameof(GatewayClientIpResolver), resolver.ToString());
        Assert.DoesNotContain("10.0.0.0", options.ToString(), StringComparison.Ordinal);
    }

    private static GatewayClientIpResolver Resolver(
        int forwardedForLimit = 1) =>
        new(new GatewayIngressOptions(
            ["10.0.0.0/8"],
            forwardedForLimit));
}
