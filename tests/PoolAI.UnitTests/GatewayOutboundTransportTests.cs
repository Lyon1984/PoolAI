using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
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
