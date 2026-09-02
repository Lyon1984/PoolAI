using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

public sealed class GatewayOutboundTransportTests
{
    [Fact]
    public async Task AuthorityLimiterSharesBudgetOnlyWithinCanonicalAuthority()
    {
        GatewayAuthorityConcurrencyLimiter limiter = new(1);
        Uri firstAuthority = new("https://upstream.example.test/v1/responses");
        Uri sameAuthority = new("https://upstream.example.test/v1/chat/completions");
        Uri otherAuthority = new("https://other.example.test/v1/responses");

        using IDisposable first = await limiter.AcquireAsync(
            firstAuthority,
            TestContext.Current.CancellationToken);
        Task<IDisposable> blocked = limiter.AcquireAsync(
                sameAuthority,
                TestContext.Current.CancellationToken)
            .AsTask();

        Assert.False(blocked.IsCompleted);
        using IDisposable independent = await limiter.AcquireAsync(
            otherAuthority,
            TestContext.Current.CancellationToken);

        first.Dispose();
        using IDisposable second = await blocked.WaitAsync(
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AdapterAttemptContextExposesNoPublicEvidenceMutator()
    {
        MethodInfo[] publicMethods = typeof(AdapterAttemptContext).GetMethods(
            BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(
            publicMethods,
            static method => method.Name.StartsWith(
                "Mark",
                StringComparison.Ordinal));
    }

    [Fact]
    public void PrimaryHandlerDisablesAmbientTransportFeaturesAndPooling()
    {
        Uri destination = new("https://upstream.example.test/v1/responses");
        using HttpRequestMessage request = new(HttpMethod.Post, destination);
        using FakeCredential credential = new();
        GatewayOutboundTransport.SendState state = new(
            request,
            destination,
            credential);

        using SocketsHttpHandler handler =
            GatewayOutboundTransport.CreatePrimaryHandler(
                new GatewayOutboundTransportOptions(
                    TimeSpan.FromSeconds(3),
                    allowLoopbackHttp: false),
                new NeverDnsResolver(),
                state);

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Equal(TimeSpan.FromSeconds(3), handler.ConnectTimeout);
        Assert.Null(handler.Credentials);
        Assert.False(handler.EnableMultipleHttp2Connections);
        Assert.Equal(256, handler.MaxConnectionsPerServer);
        Assert.NotNull(handler.PlaintextStreamFilter);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Proxy);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.TargetHost);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Host")]
    [InlineData("Cookie")]
    [InlineData("Connection")]
    [InlineData("Transfer-Encoding")]
    public void AdapterCannotPrepareSensitiveOrHopByHopHeader(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new PreparedUpstreamHeader(name, "retained-value"));
    }

    [Fact]
    public void AddressPolicyRejectsEmptyMixedAndNonExactLoopbackAnswers()
    {
        GatewayOutboundTransportOptions development = new(
            TimeSpan.FromSeconds(2),
            allowLoopbackHttp: true);
        Uri loopback = new("http://localhost:5000/v1/responses");

        Assert.False(GatewayUpstreamAddressClassifier.AreAllAllowed(
            loopback,
            [],
            development));
        Assert.False(GatewayUpstreamAddressClassifier.AreAllAllowed(
            loopback,
            [IPAddress.Loopback, IPAddress.Parse("8.8.8.8")],
            development));
        Assert.False(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("http://localhost.example.test:5000/v1/responses"),
            [IPAddress.Loopback],
            development));
        Assert.True(GatewayUpstreamAddressClassifier.AreAllAllowed(
            loopback,
            [IPAddress.Loopback],
            development));
    }

    [Fact]
    public void AddressPolicyRequiresHttpsAndExactRuleForPrivateDestination()
    {
        Assert.True(GatewayPrivateEgressRule.TryParse(
            "https://private.example.test:443|10.24.0.0/16",
            out GatewayPrivateEgressRule privateRule));
        GatewayOutboundTransportOptions production = new(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(120),
            MaxConnectionsPerServer: 256,
            AllowLoopbackHttp: false,
            PrivateEgressRules: [privateRule]);

        Assert.True(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("https://private.example.test/v1/responses"),
            [IPAddress.Parse("10.24.2.3")],
            production));
        Assert.False(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("https://other.example.test/v1/responses"),
            [IPAddress.Parse("10.24.2.3")],
            production));
        Assert.False(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("http://localhost:5000/v1/responses"),
            [IPAddress.Loopback],
            production));
    }

    [Fact]
    public void TransportConfigurationUsesBoundedValuesAndCanonicalPrivateRules()
    {
        IConfiguration configuration = Configuration(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Gateway:ConnectTimeoutSeconds"] = "7",
                ["Gateway:FirstByteTimeoutSeconds"] = "11",
                ["Gateway:StreamIdleTimeoutSeconds"] = "22",
                ["Gateway:MaxConnectionsPerServer"] = "32",
                ["Supply:Health:PrivateEgressRules:0"] =
                    "https://private.example.test:8443|10.24.0.0/16",
            });

        GatewayOutboundTransportOptions test =
            GatewayOutboundTransportOptions.FromConfiguration(
                configuration,
                "tEsT");
        GatewayOutboundTransportOptions production =
            GatewayOutboundTransportOptions.FromConfiguration(
                configuration,
                "Production");

        Assert.Equal(TimeSpan.FromSeconds(7), test.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(11), test.FirstByteTimeout);
        Assert.Equal(TimeSpan.FromSeconds(22), test.StreamIdleTimeout);
        Assert.Equal(32, test.MaxConnectionsPerServer);
        Assert.True(test.AllowLoopbackHttp);
        Assert.False(production.AllowLoopbackHttp);
        GatewayPrivateEgressRule rule = Assert.Single(test.PrivateEgressRules);
        Assert.Equal(
            "https://private.example.test:8443|10.24.0.0/16",
            rule.CanonicalKey);

        GatewayOutboundTransportOptions direct = new(
            TimeSpan.FromSeconds(2),
            allowLoopbackHttp: false,
            [rule]);
        Assert.Same(rule, Assert.Single(direct.PrivateEgressRules));
    }

    [Theory]
    [InlineData("Gateway:ConnectTimeoutSeconds", "0")]
    [InlineData("Gateway:ConnectTimeoutSeconds", "61")]
    [InlineData("Gateway:FirstByteTimeoutSeconds", "4")]
    [InlineData("Gateway:FirstByteTimeoutSeconds", "301")]
    [InlineData("Gateway:StreamIdleTimeoutSeconds", "14")]
    [InlineData("Gateway:StreamIdleTimeoutSeconds", "601")]
    [InlineData("Gateway:MaxConnectionsPerServer", "15")]
    [InlineData("Gateway:MaxConnectionsPerServer", "4097")]
    [InlineData("Supply:Health:PrivateEgressRules", "scalar-is-invalid")]
    [InlineData("Supply:Health:PrivateEgressRules:00", "https://private.example.test|10.0.0.0/8")]
    [InlineData("Supply:Health:PrivateEgressRules:1", "https://private.example.test|10.0.0.0/8")]
    [InlineData("Supply:Health:PrivateEgressRules:0:Nested", "https://private.example.test|10.0.0.0/8")]
    [InlineData("Supply:Health:PrivateEgressRules:0", "not-a-rule")]
    public void TransportConfigurationRejectsOutOfContractValues(
        string key,
        string value)
    {
        IConfiguration configuration = Configuration(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [key] = value,
            });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => GatewayOutboundTransportOptions.FromConfiguration(
                configuration,
                "Production"));

        Assert.StartsWith("The Gateway ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransportConfigurationRejectsDuplicateAndExcessivePrivateRules()
    {
        Dictionary<string, string?> duplicate = new(StringComparer.Ordinal)
        {
            ["Supply:Health:PrivateEgressRules:0"] =
                "https://private.example.test|10.0.0.0/8",
            ["Supply:Health:PrivateEgressRules:1"] =
                "https://private.example.test:443|10.0.0.0/8",
        };
        Dictionary<string, string?> excessive = Enumerable.Range(0, 65)
            .ToDictionary(
                static index => $"Supply:Health:PrivateEgressRules:{index}",
                static index => (string?)$"https://private-{index}.example.test|10.{index}.0.0/16",
                StringComparer.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            GatewayOutboundTransportOptions.FromConfiguration(
                Configuration(duplicate),
                "Production"));
        Assert.Throws<InvalidOperationException>(() =>
            GatewayOutboundTransportOptions.FromConfiguration(
                Configuration(excessive),
                "Production"));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("0.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.1.2", false)]
    [InlineData("192.0.0.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.51.100.1", false)]
    [InlineData("203.0.113.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("::", false)]
    [InlineData("::2", false)]
    [InlineData("100::1", false)]
    [InlineData("2001:2::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    public void AddressPolicyRejectsReservedRangesAndAllowsPublicAddresses(
        string address,
        bool expected)
    {
        GatewayOutboundTransportOptions production = new(
            TimeSpan.FromSeconds(2),
            allowLoopbackHttp: false);

        Assert.Equal(
            expected,
            GatewayUpstreamAddressClassifier.AreAllAllowed(
                new Uri("https://public.example.test/v1/responses"),
                [IPAddress.Parse(address)],
                production));
    }

    [Fact]
    public void AddressPolicySupportsOnlyExactDevelopmentIpv6Loopback()
    {
        GatewayOutboundTransportOptions development = new(
            TimeSpan.FromSeconds(2),
            allowLoopbackHttp: true);

        Assert.True(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("http://[::1]:5000/v1/responses"),
            [IPAddress.IPv6Loopback],
            development));
        Assert.True(GatewayUpstreamAddressClassifier.AreAllAllowed(
            new Uri("http://[::1]:5000/v1/responses"),
            [IPAddress.Parse("::ffff:127.0.0.2")],
            development));
    }

    [Fact]
    public void CidrCanonicalizationHandlesPartialMasksAndMalformedInput()
    {
        Assert.True(GatewayIpCidr.TryParseCanonical(
            "10.0.0.0/9",
            allowZeroPrefix: false,
            out GatewayIpCidr? cidr));
        GatewayIpCidr parsed = Assert.IsType<GatewayIpCidr>(cidr);

        Assert.True(parsed.Contains(IPAddress.Parse("10.127.255.255")));
        Assert.False(parsed.Contains(IPAddress.Parse("10.128.0.1")));
        Assert.False(parsed.Contains(IPAddress.Parse("fd00::1")));
        Assert.Equal(nameof(GatewayIpCidr), parsed.ToString());
        Assert.False(GatewayIpCidr.TryParseCanonical(
            "10.0.0.0/33",
            allowZeroPrefix: false,
            out _));
        Assert.False(GatewayIpCidr.TryParseCanonical(
            "2001:db8::/129",
            allowZeroPrefix: false,
            out _));
        Assert.False(GatewayIpCidr.TryParseCanonical(
            "10.0.0/24",
            allowZeroPrefix: false,
            out _));
        Assert.False(GatewayIpCidr.TryParseCanonical(
            "10.x.0.0/24",
            allowZeroPrefix: false,
            out _));
    }

    [Fact]
    public void AdapterAttemptContextRejectsInvalidIdentityAndEnforcesFenceOrder()
    {
        AdapterRouteSnapshot route = new(
            EntityId.New(),
            EntityId.New(),
            EntityId.New(),
            UpstreamType.OpenAi,
            "gpt-4.1",
            "gpt-4.1",
            new Uri("https://upstream.example.test"),
            SupportsResponses: true,
            SupportsChatCompletions: true,
            SupportsFunctionTools: true,
            SupportsStreaming: true,
            SupplyConfigurationVersion: 1,
            ChannelVersion: 1,
            AccountVersion: 1,
            CredentialRevision: 1);
        EntityId attemptId = EntityId.New();
        DateTimeOffset deadline = new(2030, 1, 1, 0, 1, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new AdapterAttemptContext(
            new EntityId(Guid.NewGuid()),
            attemptId,
            attemptIndex: 0,
            route,
            deadline,
            remainingRetryBudget: 0));

        AdapterAttemptContext context = new(
            EntityId.New(),
            attemptId,
            attemptIndex: 0,
            route,
            deadline,
            remainingRetryBudget: 0);
        Assert.Throws<InvalidOperationException>(context.MarkRequestBytesWritten);
        Assert.Throws<InvalidOperationException>(() => _ = context.OutputEvidenceSink);
        context.MarkDispatchedAfterFence();
        context.MarkRequestBytesWritten();

        Assert.True(context.RequestBytesWritten);
        Assert.Equal(nameof(AdapterAttemptContext), context.ToString());
        Assert.Equal(nameof(AdapterRouteSnapshot), route.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" https://private.example.test|10.0.0.0/8")]
    [InlineData("https://private.example.test|10.0.0.0/8 ")]
    [InlineData("https://privaté.example.test|10.0.0.0/8")]
    [InlineData("https://private\\host.example.test|10.0.0.0/8")]
    [InlineData("https://private.example.test")]
    [InlineData("https://private.example.test|10.0.0.0/8|extra")]
    [InlineData("http://private.example.test|10.0.0.0/8")]
    [InlineData("https://user@private.example.test|10.0.0.0/8")]
    [InlineData("https://private.example.test/path|10.0.0.0/8")]
    [InlineData("https://private.example.test?query=1|10.0.0.0/8")]
    [InlineData("https://private.example.test#fragment|10.0.0.0/8")]
    [InlineData("https://localhost|10.0.0.0/8")]
    [InlineData("https://127.0.0.1|10.0.0.0/8")]
    [InlineData("https://private.example.test|8.8.8.0/24")]
    [InlineData("https://private.example.test|10.0.0.0/7")]
    [InlineData("https://private.example.test|172.16.0.0/11")]
    [InlineData("https://private.example.test|192.168.0.0/15")]
    [InlineData("https://private.example.test|fc00::/6")]
    public void PrivateEgressRuleRejectsNonCanonicalOrNonPrivateInput(string value)
    {
        Assert.False(GatewayPrivateEgressRule.TryParse(value, out _));
    }

    [Fact]
    public void PrivateIpv6RuleMatchesOnlyItsCanonicalAuthorityAndNetwork()
    {
        Assert.True(GatewayPrivateEgressRule.TryParse(
            "https://[fd00::1234]:8443|fd00::/8",
            out GatewayPrivateEgressRule rule));

        Assert.Equal("https://[fd00::1234]:8443|fd00::/8", rule.CanonicalKey);
        Assert.True(rule.Matches(
            new Uri("https://[fd00::1234]:8443/v1/responses"),
            IPAddress.Parse("fd12::1")));
        Assert.False(rule.Matches(
            new Uri("https://[fd00::1234]:443/v1/responses"),
            IPAddress.Parse("fd12::1")));
        Assert.False(rule.Matches(
            new Uri("https://[fd00::1234]:8443/v1/responses"),
            IPAddress.Parse("fe00::1")));
    }

    [Fact]
    public void PreparedTransportValuesEnforceTargetAndHeaderBounds()
    {
        PreparedUpstreamHeader header = new("x-test", "safe\tvalue");
        using PreparedUpstreamRequest request = new(
            HttpMethod.Post,
            new Uri("https://upstream.example.test/v1/responses"),
            [1, 2, 3],
            [header]);
        Assert.Equal(nameof(PreparedUpstreamHeader), header.ToString());
        Assert.Equal(nameof(PreparedUpstreamRequest), request.ToString());
        Assert.Equal([1, 2, 3], request.Body.ToArray());

        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Put,
            new Uri("https://upstream.example.test"),
            []));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Get,
            new Uri("https://upstream.example.test"),
            [1]));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("relative", UriKind.Relative),
            []));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("ftp://upstream.example.test"),
            []));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("https://user@upstream.example.test"),
            []));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("https://upstream.example.test/#fragment"),
            []));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("https://upstream.example.test"),
            [],
            [header, new PreparedUpstreamHeader("X-Test", "duplicate")]));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("https://upstream.example.test"),
            [],
            Enumerable.Repeat(header, 65)));
        Assert.Throws<ArgumentException>(() => new PreparedUpstreamRequest(
            HttpMethod.Post,
            new Uri("https://upstream.example.test"),
            [],
            new PreparedUpstreamHeader[] { null! }));

        request.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = request.Body);
    }

    [Fact]
    public void UpstreamResponseEnforcesStatusAndHeaderBounds()
    {
        using MemoryStream content = new([4, 5, 6]);
        string[] expectedValues = ["one", "two"];
        AdapterUpstreamResponse response = new(
            200,
            content,
            [new("X-Test", expectedValues)]);
        Assert.True(response.TryGetHeader("x-test", out var values));
        Assert.Equal(expectedValues, values);
        Assert.False(response.TryGetHeader("missing", out _));
        Assert.Equal(nameof(AdapterUpstreamResponse), response.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdapterUpstreamResponse(99, Stream.Null, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdapterUpstreamResponse(600, Stream.Null, []));
        Assert.Throws<ArgumentException>(() => new AdapterUpstreamResponse(
            200,
            Stream.Null,
            [new("", Array.Empty<string>())]));
        Assert.Throws<ArgumentException>(() => new AdapterUpstreamResponse(
            200,
            Stream.Null,
            [
                new("X-Test", Enumerable.Repeat("one", 1)),
                new("x-test", Enumerable.Repeat("two", 1)),
            ]));
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, false)]
    [InlineData(429, true)]
    [InlineData(500, false)]
    public void RejectedStatusEvidenceIsExplicitAndStatusSpecific(
        int statusCode,
        bool expected)
    {
        AdapterCapability capability = new(
            InboundProtocol.Responses,
            UpstreamType.OpenAi,
            AdapterOperation.NonStream,
            CanProveNoRequestBytesWritten: true,
            SupportsVerifiedIdempotentReplay: false,
            AdapterRejectedStatusEvidence.Unauthorized
                | AdapterRejectedStatusEvidence.TooManyRequests);

        Assert.Equal(
            expected,
            capability.ConfirmsNoExecutionForStatus(statusCode));
    }

    private static IConfiguration Configuration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private sealed class FakeCredential : ITransportCredentialHandle
    {
        public ITransportCredentialAttachment AttachAuthorizationOnce(
            Uri vettedDestination,
            HttpRequestMessage transportOwnedRequest)
        {
            transportOwnedRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", "test-value");
            return new FakeAttachment(transportOwnedRequest);
        }

        public void Dispose()
        {
        }

        private sealed class FakeAttachment(HttpRequestMessage request) :
            ITransportCredentialAttachment
        {
            private HttpRequestMessage? _request = request;

            public void Dispose()
            {
                HttpRequestMessage? current = Interlocked.Exchange(
                    ref _request,
                    null);
                if (current is not null)
                {
                    current.Headers.Authorization = null;
                }
            }
        }
    }

    private sealed class NeverDnsResolver : IGatewayDnsResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "DNS resolution is not expected in this test.");
    }
}
