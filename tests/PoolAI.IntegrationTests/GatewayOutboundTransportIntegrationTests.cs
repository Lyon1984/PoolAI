using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;
using PoolAI.Modules.Routing.Abstractions;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.IntegrationTests;

// Governing contracts: ADR 0011 connection-time SSRF fence and ADR 0015
// revision-fenced, transport-owned credential handoff.
public sealed class GatewayOutboundTransportIntegrationTests
{
    [Fact]
    public async Task VetsBeforeAuthorizationAndPreservesOriginalAuthority()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> server = ServeOnceAsync(
            listener,
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}",
            timeout.Token);
        ConcurrentQueue<string> events = new();
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            events);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        string request = await server;
        listener.Stop();

        Assert.True(result.Response.IsSuccess);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
        Assert.Contains(
            $"Host: localhost:{port}\r\n",
            request,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Authorization: Bearer deterministic-upstream-key\r\n",
            request,
            StringComparison.Ordinal);
        Assert.Contains(
            "Connection: close\r\n",
            request,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(IndexOf(events, "dns") < IndexOf(events, "credential"));
        Assert.True(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task EmptyMixedAndProductionHttpAnswersFailBeforeCredentialUse()
    {
        await AssertRejectedBeforeCredentialAsync(
            [],
            allowLoopbackHttp: true);
        await AssertRejectedBeforeCredentialAsync(
            [IPAddress.Loopback, IPAddress.Parse("8.8.8.8")],
            allowLoopbackHttp: true);
        await AssertRejectedBeforeCredentialAsync(
            [IPAddress.Loopback],
            allowLoopbackHttp: false);
    }

    [Fact]
    public async Task CrossAuthorityIsRejectedBeforeDnsOrCredentialUse()
    {
        ConcurrentQueue<string> events = new();
        using Fixture fixture = new(
            new Uri("http://localhost:49151/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            events);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri("http://127.0.0.1:49151/v1/responses"),
            TestContext.Current.CancellationToken);
        fixture.DisposeCredential();

        Assert.True(result.Response.IsFailure);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedNotWritten,
            result.WriteEvidence);
        Assert.True(result.ConfirmedNoExecution);
        Assert.DoesNotContain("dns", events);
        Assert.DoesNotContain("credential", events);
        Assert.False(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CapabilityFalseCannotTurnPreWriteFailureIntoZeroSettlementEvidence()
    {
        using Fixture fixture = new(
            new Uri("http://localhost:49152/v1/"),
            [],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: false,
            new ConcurrentQueue<string>());

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri("http://localhost:49152/v1/responses"),
            TestContext.Current.CancellationToken);
        fixture.DisposeCredential();

        Assert.True(result.Response.IsFailure);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedNotWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
    }

    [Theory]
    [InlineData(true, AdapterRejectedStatusEvidence.None, false)]
    [InlineData(false, AdapterRejectedStatusEvidence.Unauthorized, true)]
    [InlineData(true, AdapterRejectedStatusEvidence.Unauthorized, true)]
    public async Task RejectionEvidenceRequiresExplicitStatusCapability(
        bool canProveNoRequestBytesWritten,
        AdapterRejectedStatusEvidence rejectedStatusEvidence,
        bool expectedConfirmedNoExecution)
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> server = ServeOnceAsync(
            listener,
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten,
            new ConcurrentQueue<string>(),
            rejectedStatusEvidence);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        _ = await server;
        listener.Stop();

        Assert.True(result.Response.IsSuccess);
        Assert.Equal(401, result.Response.Value.StatusCode);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
        Assert.Equal(
            expectedConfirmedNoExecution,
            result.ConfirmedNoExecution);
    }

    [Fact]
    public async Task ParsedFailureOnRegisteredRejectionDoesNotPublishNoExecution()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> server = ServeOnceAsync(
            listener,
            "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            new ConcurrentQueue<string>(),
            AdapterRejectedStatusEvidence.Unauthorized,
            parseFailure: true);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        _ = await server;
        listener.Stop();

        Assert.True(result.Response.IsFailure);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
    }

    [Fact]
    public async Task ParserExceptionPreservesConfirmedWrittenEvidence()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> server = ServeOnceAsync(
            listener,
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}",
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            new ConcurrentQueue<string>(),
            throwDuringParse: true);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        _ = await server;
        listener.Stop();

        Assert.True(result.Response.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Response.Error.Code);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
        Assert.True(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task RequestCreationExceptionPreservesConfirmedNotWrittenEvidence()
    {
        ConcurrentQueue<string> events = new();
        using Fixture fixture = new(
            new Uri("http://localhost:49154/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            events,
            throwDuringCreate: true);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri("http://localhost:49154/v1/responses"),
            TestContext.Current.CancellationToken);
        fixture.DisposeCredential();

        Assert.True(result.Response.IsFailure);
        Assert.Equal("upstream_protocol_error", result.Response.Error.Code);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedNotWritten,
            result.WriteEvidence);
        Assert.True(result.ConfirmedNoExecution);
        Assert.DoesNotContain("dns", events);
        Assert.DoesNotContain("credential", events);
        Assert.False(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task HttpsVettedDirectSocketPreservesOriginalSniHostAndCertificateName()
    {
        TlsAuthorityScenario result = await RunTlsAuthorityScenarioAsync(
            "upstream.poolai.test");

        Assert.Null(result.Failure);
        Assert.True(result.Success);
        Assert.Equal("upstream.poolai.test", result.ObservedSni);
        Assert.Contains(
            $"Host: upstream.poolai.test:{result.Port}\r\n",
            result.Request,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Authorization: Bearer deterministic-tls-key\r\n",
            result.Request,
            StringComparison.Ordinal);
        Assert.True(result.CredentialAttached);
    }

    [Fact]
    public async Task HttpsCertificateNameMismatchFailsBeforeCredentialOrHttpBytes()
    {
        TlsAuthorityScenario result = await RunTlsAuthorityScenarioAsync(
            "wrong.poolai.test");

        Assert.NotNull(result.Failure);
        Assert.False(result.Success);
        Assert.Equal("upstream.poolai.test", result.ObservedSni);
        Assert.True(string.IsNullOrEmpty(result.Request));
        Assert.False(result.CredentialAttached);
    }

    [Fact]
    public async Task RedirectIsReturnedToAdapterAndNeverFollowed()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<string> server = ServeOnceAsync(
            listener,
            "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/stolen\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            new ConcurrentQueue<string>());

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        _ = await server;
        listener.Stop();

        Assert.True(result.Response.IsSuccess);
        Assert.Equal(302, result.Response.Value.StatusCode);
        Assert.Equal(1, fixture.DnsResolver.Calls);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
    }

    [Fact]
    public async Task FirstByteTimeoutAfterRequestWriteIsNeverNoExecutionEvidence()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource releaseServer = new();
        Task<string> server = ReceiveAndHoldAsync(
            listener,
            releaseServer.Token,
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            new ConcurrentQueue<string>(),
            firstByteTimeout: TimeSpan.FromMilliseconds(100));

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        releaseServer.Cancel();
        string request = await server;
        listener.Stop();

        Assert.True(result.Response.IsFailure);
        Assert.Equal("upstream_unavailable", result.Response.Error.Code);
        Assert.Equal(
            GatewayRequestWriteEvidence.PossiblyWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
        Assert.Contains("Authorization: Bearer", request, StringComparison.Ordinal);
        Assert.True(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task StreamIdleTimeoutAfterHeadersIsNeverNoExecutionEvidence()
    {
        TcpListener listener = StartLoopbackListener(out int port);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource releaseServer = new();
        Task<string> server = ServePartialAndHoldAsync(
            listener,
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{",
            releaseServer.Token,
            timeout.Token);
        using Fixture fixture = new(
            new Uri($"http://localhost:{port}/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            new ConcurrentQueue<string>(),
            consumeResponseBody: true,
            streamIdleTimeout: TimeSpan.FromMilliseconds(100));

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri($"http://localhost:{port}/v1/responses"),
            timeout.Token);
        releaseServer.Cancel();
        _ = await server;
        listener.Stop();

        Assert.True(result.Response.IsFailure);
        Assert.Equal("upstream_unavailable", result.Response.Error.Code);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedWritten,
            result.WriteEvidence);
        Assert.False(result.ConfirmedNoExecution);
        Assert.True(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ExpiredAttemptDeadlinePreventsDnsCredentialAndUpstreamIo()
    {
        ConcurrentQueue<string> events = new();
        using Fixture fixture = new(
            new Uri("http://localhost:49153/v1/"),
            [IPAddress.Loopback],
            allowLoopbackHttp: true,
            canProveNoRequestBytesWritten: true,
            events,
            deadline: TimeProvider.System.GetUtcNow().AddSeconds(-1));

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri("http://localhost:49153/v1/responses"),
            TestContext.Current.CancellationToken);
        fixture.DisposeCredential();

        Assert.True(result.Response.IsFailure);
        Assert.Equal("upstream_unavailable", result.Response.Error.Code);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedNotWritten,
            result.WriteEvidence);
        Assert.True(result.ConfirmedNoExecution);
        Assert.DoesNotContain("dns", events);
        Assert.DoesNotContain("credential", events);
        Assert.False(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    private static async Task AssertRejectedBeforeCredentialAsync(
        IPAddress[] addresses,
        bool allowLoopbackHttp)
    {
        ConcurrentQueue<string> events = new();
        using Fixture fixture = new(
            new Uri("http://localhost:49150/v1/"),
            addresses,
            allowLoopbackHttp,
            canProveNoRequestBytesWritten: true,
            events);

        GatewayUpstreamTransportResult result = await fixture.SendAsync(
            new Uri("http://localhost:49150/v1/responses"),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
        fixture.DisposeCredential();

        Assert.True(result.Response.IsFailure);
        Assert.Equal(
            GatewayRequestWriteEvidence.ConfirmedNotWritten,
            result.WriteEvidence);
        Assert.True(result.ConfirmedNoExecution);
        Assert.DoesNotContain("credential", events);
        Assert.False(fixture.CredentialLease.Transferred);
        Assert.All(
            fixture.CredentialLease.Buffer,
            static value => Assert.Equal(0, value));
    }

    private static TcpListener StartLoopbackListener(out int port)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private static async Task<TlsAuthorityScenario>
        RunTlsAuthorityScenarioAsync(string certificateDnsName)
    {
        const string requestedHost = "upstream.poolai.test";
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start(1);
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        (X509Certificate2 Root, X509Certificate2 Server) certificates =
            CreateServerCertificate(certificateDnsName);
        using X509Certificate2 root = certificates.Root;
        using X509Certificate2 serverCertificate = certificates.Server;
        TaskCompletionSource<string?> observedSni = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> server = ServeTlsOnceAsync(
            listener,
            serverCertificate,
            observedSni,
            timeout.Token);
        Uri destination = new(
            $"https://{requestedHost}:{port}/v1/responses");
        using RecordingTransportCredential credential = new();
        TlsClientResult clientResult = await SendTlsRequestAsync(
            destination,
            port,
            root,
            credential,
            timeout.Token).ConfigureAwait(false);
        string? received = await server.ConfigureAwait(false);
        listener.Stop();
        return new TlsAuthorityScenario(
            port,
            clientResult.Success,
            clientResult.Failure,
            await observedSni.Task.WaitAsync(timeout.Token).ConfigureAwait(false),
            received,
            credential.Attached);
    }

    private static async Task<TlsClientResult> SendTlsRequestAsync(
        Uri destination,
        int port,
        X509Certificate2 root,
        RecordingTransportCredential credential,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, destination);
        request.Headers.ConnectionClose = true;
        GatewayOutboundTransport.SendState state = new(
            request,
            destination,
            credential);
        using SocketsHttpHandler handler =
            GatewayOutboundTransport.CreatePrimaryHandler(
                new GatewayOutboundTransportOptions(
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(3),
                    TimeSpan.FromSeconds(3),
                    MaxConnectionsPerServer: 1,
                    AllowLoopbackHttp: false,
                    PrivateEgressRules: []),
                new StaticDnsResolver(
                    [IPAddress.Loopback],
                    new ConcurrentQueue<string>()),
                state);
        // The production connect-time SSRF fence is covered separately. This
        // loopback connector isolates the post-fence TLS, SNI, and credential
        // boundary without exposing a test listener to the local network.
        handler.ConnectCallback = (context, token) =>
            ConnectTlsTestLoopbackAsync(
                context,
                destination,
                port,
                state,
                token);
        handler.SslOptions.CertificateChainPolicy = CreateTrustPolicy(root);
        using HttpMessageInvoker client = new(handler, disposeHandler: false);
        Exception? failure = null;
        bool success = false;
        try
        {
            using HttpResponseMessage response = await client.SendAsync(
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            success = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or AuthenticationException
                or OperationCanceledException)
        {
            failure = exception;
        }
        finally
        {
            state.ClearCredentialAttachment();
        }
        return new TlsClientResult(success, failure);
    }

    private static async ValueTask<Stream> ConnectTlsTestLoopbackAsync(
        SocketsHttpConnectionContext context,
        Uri destination,
        int port,
        GatewayOutboundTransport.SendState state,
        CancellationToken cancellationToken)
    {
        Assert.Equal(destination.IdnHost, context.DnsEndPoint.Host);
        Assert.Equal(port, context.DnsEndPoint.Port);
        Socket socket = new(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(
                    new IPEndPoint(IPAddress.Loopback, port),
                    cancellationToken)
                .ConfigureAwait(false);
            state.MarkDirectConnectionEstablished();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<string?> ServeTlsOnceAsync(
        TcpListener listener,
        X509Certificate2 serverCertificate,
        TaskCompletionSource<string?> observedSni,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using NetworkStream network = client.GetStream();
        using SslStream tls = new(network, leaveInnerStreamOpen: false);
        SslServerAuthenticationOptions options = new()
        {
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ClientCertificateRequired = false,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ServerCertificate = serverCertificate,
        };
        try
        {
            await tls.AuthenticateAsServerAsync(options, cancellationToken)
                .ConfigureAwait(false);
            observedSni.TrySetResult(tls.TargetHostName);
            string request = await ReadRequestAsync(tls, cancellationToken)
                .ConfigureAwait(false);
            if (request.Length == 0)
            {
                return null;
            }

            await tls.WriteAsync(
                    "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8
                        .ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            return request;
        }
        catch (Exception exception) when (
            exception is AuthenticationException
                or IOException
                or OperationCanceledException)
        {
            return null;
        }
    }

    private static X509ChainPolicy CreateTrustPolicy(
        X509Certificate2 root)
    {
        X509ChainPolicy policy = new()
        {
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck,
            TrustMode = X509ChainTrustMode.CustomRootTrust,
        };
        policy.CustomTrustStore.Add(root);
        return policy;
    }

    private static (X509Certificate2 Root, X509Certificate2 Server)
        CreateServerCertificate(string dnsName)
    {
        DateTimeOffset now = TimeProvider.System.GetUtcNow();
        RSA rootKey = RSA.Create(2048);
        CertificateRequest rootRequest = new(
            "CN=PoolAI Integration Test Root",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign
                    | X509KeyUsageFlags.CrlSign,
                true));
        X509Certificate2 root = rootRequest.CreateSelfSigned(
            now.AddMinutes(-5),
            now.AddHours(1));
        rootKey.Dispose();

        using RSA serverKey = RSA.Create(2048);
        CertificateRequest serverRequest = new(
            $"CN={dnsName}",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(dnsName);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                true));
        OidCollection usages = new();
        usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        serverRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(usages, true));
        using X509Certificate2 issued = serverRequest.Create(
            root,
            now.AddMinutes(-5),
            now.AddHours(1),
            RandomNumberGenerator.GetBytes(16));
        return (root, issued.CopyWithPrivateKey(serverKey));
    }

    private static async Task<string> ServeOnceAsync(
        TcpListener listener,
        string response,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        byte[] received = new byte[16 * 1024];
        int length = 0;
        int expectedLength = int.MaxValue;
        while (length < expectedLength)
        {
            int read = await stream.ReadAsync(
                    received.AsMemory(length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            int headerEnd = HeaderEnd(received.AsSpan(0, length));
            if (headerEnd >= 0)
            {
                expectedLength = checked(
                    headerEnd + 4 + ContentLength(received.AsSpan(0, headerEnd)));
            }
        }

        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(responseBytes, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.ASCII.GetString(received, 0, length);
    }

    private static async Task<string> ReceiveAndHoldAsync(
        TcpListener listener,
        CancellationToken releaseToken,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        string request = await ReadRequestAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        await HoldUntilReleasedAsync(releaseToken).ConfigureAwait(false);
        return request;
    }

    private static async Task<string> ServePartialAndHoldAsync(
        TcpListener listener,
        string partialResponse,
        CancellationToken releaseToken,
        CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(
                cancellationToken)
            .ConfigureAwait(false);
        using NetworkStream stream = client.GetStream();
        string request = await ReadRequestAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        await stream.WriteAsync(
                Encoding.ASCII.GetBytes(partialResponse),
                cancellationToken)
            .ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await HoldUntilReleasedAsync(releaseToken).ConfigureAwait(false);
        return request;
    }

    private static async Task<string> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] received = new byte[16 * 1024];
        int length = 0;
        int expectedLength = int.MaxValue;
        while (length < expectedLength)
        {
            int read = await stream.ReadAsync(
                    received.AsMemory(length),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            int headerEnd = HeaderEnd(received.AsSpan(0, length));
            if (headerEnd >= 0)
            {
                expectedLength = checked(
                    headerEnd + 4 + ContentLength(received.AsSpan(0, headerEnd)));
            }
        }

        return Encoding.ASCII.GetString(received, 0, length);
    }

    private static async Task HoldUntilReleasedAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static int HeaderEnd(ReadOnlySpan<byte> value)
    {
        for (int index = 0; index <= value.Length - 4; index++)
        {
            if (value[index] == '\r'
                && value[index + 1] == '\n'
                && value[index + 2] == '\r'
                && value[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static int ContentLength(ReadOnlySpan<byte> headers)
    {
        string text = Encoding.ASCII.GetString(headers);
        foreach (string line in text.Split("\r\n", StringSplitOptions.None))
        {
            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    line.AsSpan(prefix.Length).Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int length))
            {
                return length;
            }
        }

        return 0;
    }

    private static int IndexOf(
        IEnumerable<string> events,
        string expected)
    {
        int index = 0;
        foreach (string value in events)
        {
            if (string.Equals(value, expected, StringComparison.Ordinal))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IUpstreamCredentialHandle _credential;
        private readonly AdapterAttemptContext _context;
        private readonly AdapterCapability _capability;
        private readonly GatewayOutboundTransport _transport;
        private readonly bool _parseFailure;
        private readonly bool _consumeResponseBody;
        private readonly bool _throwDuringParse;
        private readonly bool _throwDuringCreate;

        internal Fixture(
            Uri baseUri,
            IPAddress[] addresses,
            bool allowLoopbackHttp,
            bool canProveNoRequestBytesWritten,
            ConcurrentQueue<string> events,
            AdapterRejectedStatusEvidence rejectedStatusEvidence =
                AdapterRejectedStatusEvidence.None,
            bool parseFailure = false,
            bool consumeResponseBody = false,
            TimeSpan? firstByteTimeout = null,
            TimeSpan? streamIdleTimeout = null,
            DateTimeOffset? deadline = null,
            bool throwDuringParse = false,
            bool throwDuringCreate = false)
        {
            EntityId groupId = EntityId.New();
            EntityId channelId = EntityId.New();
            EntityId accountId = EntityId.New();
            AccountRoute route = CreateRoute(
                groupId,
                channelId,
                accountId,
                baseUri);
            CredentialLease = new RecordingCredentialLease(events);
            GatewayCredentialHandoff handoff = new(
                new StaticCredentialSource(CredentialLease));
            _credential = handoff.AcquireAsync(
                    route,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult()
                .Value;
            _context = CreateAttemptContext(
                EntityId.New(),
                EntityId.New(),
                groupId,
                channelId,
                accountId,
                baseUri,
                deadline ?? new DateTimeOffset(
                    2030,
                    1,
                    1,
                    0,
                    2,
                    0,
                    TimeSpan.Zero));
            _context.MarkDispatchedAfterFence();
            _capability = new AdapterCapability(
                InboundProtocol.Responses,
                UpstreamType.OpenAiCompatible,
                AdapterOperation.NonStream,
                canProveNoRequestBytesWritten,
                SupportsVerifiedIdempotentReplay: false,
                rejectedStatusEvidence);
            _parseFailure = parseFailure;
            _consumeResponseBody = consumeResponseBody;
            _throwDuringParse = throwDuringParse;
            _throwDuringCreate = throwDuringCreate;
            DnsResolver = new StaticDnsResolver(addresses, events);
            _transport = new GatewayOutboundTransport(
                new GatewayOutboundTransportOptions(
                    TimeSpan.FromSeconds(2),
                    firstByteTimeout ?? TimeSpan.FromSeconds(60),
                    streamIdleTimeout ?? TimeSpan.FromSeconds(120),
                    256,
                    allowLoopbackHttp,
                    Array.Empty<GatewayPrivateEgressRule>()),
                DnsResolver,
                TimeProvider.System);
        }

        internal RecordingCredentialLease CredentialLease { get; }

        internal StaticDnsResolver DnsResolver { get; }

        internal ValueTask<GatewayUpstreamTransportResult> SendAsync(
            Uri requestUri,
            CancellationToken cancellationToken)
        {
            RecordingPreparedAttempt prepared = new(
                requestUri,
                _parseFailure,
                _consumeResponseBody,
                _throwDuringParse,
                _throwDuringCreate);
            return _transport.SendAsync(
                prepared,
                _context,
                _capability,
                _credential,
                cancellationToken);
        }

        internal void DisposeCredential() => _credential.Dispose();

        public void Dispose() => _credential.Dispose();

        private static AccountRoute CreateRoute(
            EntityId groupId,
            EntityId channelId,
            EntityId accountId,
            Uri baseUri) => new(
            groupId,
            channelId,
            accountId,
            AccountRouteProvider.OpenAiCompatible,
            "client-model",
            "upstream-model",
            baseUri,
            new AccountRouteCapabilities(true, true, true, true),
            new DateTimeOffset(2030, 1, 1, 0, 2, 0, TimeSpan.Zero),
            11,
            13,
            17,
            23);

        private static AdapterAttemptContext CreateAttemptContext(
            EntityId requestId,
            EntityId userId,
            EntityId groupId,
            EntityId channelId,
            EntityId accountId,
            Uri baseUri,
            DateTimeOffset deadline) => new(
            requestId,
            userId,
            0,
            new AdapterRouteSnapshot(
                groupId,
                channelId,
                accountId,
                UpstreamType.OpenAiCompatible,
                "client-model",
                "upstream-model",
                baseUri,
                true,
                true,
                true,
                true,
                11,
                13,
                17,
                23),
            deadline,
            0);
    }

    private sealed class RecordingPreparedAttempt(
        Uri requestUri,
        bool parseFailure,
        bool consumeResponseBody,
        bool throwDuringParse,
        bool throwDuringCreate) :
        IPreparedUpstreamAttempt
    {
        public ValueTask<Result<PreparedUpstreamRequest>> CreateRequestAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwDuringCreate)
            {
                throw new JsonException("The scripted request creator threw.");
            }

            return ValueTask.FromResult(Result.Success(
                new PreparedUpstreamRequest(
                    HttpMethod.Post,
                    requestUri,
                    "{}"u8,
                    [new PreparedUpstreamHeader("Accept", "application/json")])));
        }

        public async ValueTask<Result<NormalizedUpstreamResult>> ParseResponseAsync(
            AdapterUpstreamResponse response,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (throwDuringParse)
            {
                throw new JsonException("The scripted parser threw.");
            }

            if (parseFailure)
            {
                return Result.Failure<NormalizedUpstreamResult>(
                    "upstream_protocol_error",
                    "The scripted parser rejected the response.");
            }

            if (consumeResponseBody)
            {
                await response.Content.CopyToAsync(
                        Stream.Null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return Result.Success(
                new NormalizedUpstreamResult(
                    response.StatusCode,
                    JsonSerializer.SerializeToElement(new { }),
                    Usage: null,
                    ErrorCode: response.StatusCode is >= 200 and <= 299
                        ? null
                        : "upstream_rejected"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticDnsResolver(
        IPAddress[] addresses,
        ConcurrentQueue<string> events) : IGatewayDnsResolver
    {
        internal int Calls { get; private set; }

        public ValueTask<IPAddress[]> ResolveAsync(
            string host,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            events.Enqueue("dns");
            return ValueTask.FromResult(addresses.ToArray());
        }
    }

    private sealed class StaticCredentialSource(
        IRouteCredentialLease credential) : IRouteCredentialLeaseSource
    {
        public ValueTask<Result<IRouteCredentialLease>> AcquireAsync(
            RouteCredentialLeaseRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Success(credential));
        }
    }

    private sealed record TlsAuthorityScenario(
        int Port,
        bool Success,
        Exception? Failure,
        string? ObservedSni,
        string? Request,
        bool CredentialAttached);

    private sealed record TlsClientResult(
        bool Success,
        Exception? Failure);

    private sealed class RecordingTransportCredential :
        ITransportCredentialHandle
    {
        internal bool Attached { get; private set; }

        public ITransportCredentialAttachment AttachAuthorizationOnce(
            Uri vettedDestination,
            HttpRequestMessage transportOwnedRequest)
        {
            Attached = true;
            transportOwnedRequest.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    "deterministic-tls-key");
            return new RecordingTransportCredentialAttachment(
                transportOwnedRequest);
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingTransportCredentialAttachment(
        HttpRequestMessage request) : ITransportCredentialAttachment
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

    internal sealed class RecordingCredentialLease(
        ConcurrentQueue<string> events) : IRouteCredentialLease
    {
        private byte[]? _credential =
            "deterministic-upstream-key"u8.ToArray();

        internal byte[] Buffer { get; } =
            "deterministic-upstream-key"u8.ToArray();

        internal bool Transferred { get; private set; }

        public void TransferOnce(RouteCredentialReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);
            byte[] current = Interlocked.Exchange(ref _credential, null)
                ?? throw new ObjectDisposedException(
                    nameof(RecordingCredentialLease));
            events.Enqueue("credential");
            Transferred = true;
            try
            {
                reader(current);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(current);
                current.CopyTo(Buffer, 0);
            }
        }

        public void Dispose()
        {
            byte[]? current = Interlocked.Exchange(ref _credential, null);
            if (current is not null)
            {
                CryptographicOperations.ZeroMemory(current);
                current.CopyTo(Buffer, 0);
            }
        }
    }
}
