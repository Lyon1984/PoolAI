using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Supply.Abstractions;
using PoolAI.Modules.Supply.Application.Ports;
using PoolAI.Modules.Supply.Infrastructure.Health;

namespace PoolAI.UnitTests;

// Governing contract: ADR 0011, "Controlled active health probe" and
// "Connection-time SSRF and credential boundary".
public sealed class AccountHealthProbeHttpCoverageTests
{
    private static readonly DateTimeOffset ObservationTime = new(
        2026,
        7,
        31,
        8,
        30,
        0,
        TimeSpan.Zero);
    private static readonly Uri BaseUri = new(
        "https://upstream.example.test/v1/");
    private static readonly byte[] Credential =
        Encoding.UTF8.GetBytes("test-health-credential");

    [Fact]
    public async Task ModelsProbeUsesOriginalAuthorityAndExactBoundedRequest()
    {
        CapturedRequest captured = new();
        AccountHealthProbeHttpTransport transport = Transport(
            (request, _) =>
            {
                captured.Method = request.Method;
                captured.Uri = request.RequestUri;
                captured.Authority = request.Headers.Host;
                captured.AuthorizationScheme =
                    request.Headers.Authorization?.Scheme;
                captured.AuthorizationParameter =
                    request.Headers.Authorization?.Parameter;
                captured.Accept = request.Headers.Accept
                    .Select(value => value.MediaType)
                    .OfType<string>()
                    .ToArray();
                captured.ConnectionClose = request.Headers.ConnectionClose;
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"data":[]}"""));
            });

        AccountHealthProbeResult result = await transport.ProbeAsync(
            new Uri(
                "https://upstream.example.test:8443/v1///?discard=yes#discard"),
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountHealthProbeOutcome.Success, result.Outcome);
        Assert.Equal(200, result.UpstreamStatusCode);
        Assert.Equal(ObservationTime, result.ObservedAt);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            new Uri("https://upstream.example.test:8443/v1/models"),
            captured.Uri);
        Assert.Null(captured.Authority);
        Assert.Equal("Bearer", captured.AuthorizationScheme);
        Assert.Equal("test-health-credential", captured.AuthorizationParameter);
        Assert.Equal(["application/json"], captured.Accept);
        Assert.True(captured.ConnectionClose);
    }

    [Fact]
    public async Task PrimaryHandlerConnectsDirectlyWithoutRedirectProxyOrReuse()
    {
        using Socket listener = new(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using CancellationTokenSource serverTimeout =
            new(TimeSpan.FromSeconds(5));
        Task<string> server = ServeSingleResponseAsync(
            listener,
            serverTimeout.Token);
        AccountHealthProbeHttpOptions options = new(
            Timeout: TimeSpan.FromSeconds(2),
            MaximumResponseBytes: 1_048_576,
            AllowLoopbackHttp: true);
        using SocketsHttpHandler primary =
            AccountHealthProbeHttpTransport.CreatePrimaryHandler(options);
        Assert.False(primary.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, primary.AutomaticDecompression);
        Assert.False(primary.UseCookies);
        Assert.False(primary.UseProxy);
        Assert.Equal(TimeSpan.Zero, primary.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.Zero, primary.PooledConnectionLifetime);
        Assert.NotNull(primary.ConnectCallback);
        Assert.Null(primary.SslOptions.RemoteCertificateValidationCallback);
        AccountHealthProbeHttpTransport transport = Transport(
            primary,
            options);

        AccountHealthProbeResult result = await transport.ProbeAsync(
            new Uri($"http://127.0.0.1:{port}/tenant/"),
            Credential,
            TestContext.Current.CancellationToken);
        string requestHeaders = await server;

        Assert.Equal(AccountHealthProbeOutcome.Success, result.Outcome);
        Assert.StartsWith(
            "GET /tenant/models HTTP/1.1\r\n",
            requestHeaders,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Host: 127.0.0.1:{port}\r\n",
            requestHeaders,
            StringComparison.Ordinal);
        Assert.Contains(
            "Authorization: Bearer test-health-credential\r\n",
            requestHeaders,
            StringComparison.Ordinal);
        Assert.Contains(
            "Connection: close\r\n",
            requestHeaders,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrimaryHandlerRejectsLoopbackWhenEnvironmentPolicyForbidsIt()
    {
        AccountHealthProbeHttpOptions options = new(
            Timeout: TimeSpan.FromSeconds(2),
            MaximumResponseBytes: 1_048_576,
            AllowLoopbackHttp: false);
        using SocketsHttpHandler primary =
            AccountHealthProbeHttpTransport.CreatePrimaryHandler(options);
        AccountHealthProbeHttpTransport transport = Transport(
            primary,
            options);

        AccountHealthProbeResult result = await transport.ProbeAsync(
            new Uri("http://127.0.0.1:1/v1"),
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            result.Outcome);
        Assert.Null(result.UpstreamStatusCode);
    }

    [Theory]
    [InlineData("""{"data":[]}""", true)]
    [InlineData("""{"data":[{"id":"model"}]}""", true)]
    [InlineData("""{}""", false)]
    [InlineData("""{"data":{}}""", false)]
    [InlineData("""[]""", false)]
    [InlineData("""{"DATA":[]}""", false)]
    [InlineData("""{"data":""", false)]
    public async Task Http200RequiresBoundedJsonObjectWithDataArray(
        string body,
        bool success)
    {
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, body)));

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            success
                ? AccountHealthProbeOutcome.Success
                : AccountHealthProbeOutcome.TransientFailure,
            result.Outcome);
        Assert.Equal(200, result.UpstreamStatusCode);
        Assert.Null(result.RetryAfter);
    }

    [Fact]
    public async Task DeclaredOversizedSuccessIsRejectedBeforeReadingBody()
    {
        ThrowOnReadStream body = new(new IOException("must not be read"));
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) =>
            {
                StreamContent content = new(body);
                content.Headers.ContentLength = 65;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                });
            },
            maximumResponseBytes: 64);

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            result.Outcome);
        Assert.Equal(0, body.ReadCount);
    }

    [Fact]
    public async Task UndeclaredOversizedOrTruncatedSuccessIsTransientFailure()
    {
        AccountHealthProbeHttpTransport oversized = Transport(
            (_, _) => Task.FromResult(
                StreamResponse(
                    HttpStatusCode.OK,
                    new MemoryStream(new byte[65]))),
            maximumResponseBytes: 64);
        AccountHealthProbeHttpTransport truncated = Transport(
            (_, _) => Task.FromResult(
                StreamResponse(
                    HttpStatusCode.OK,
                    new PrefixThenThrowStream(
                        Encoding.UTF8.GetBytes("""{"data":["""),
                        new IOException("truncated")))),
            maximumResponseBytes: 64);

        AccountHealthProbeResult oversizedResult =
            await oversized.ProbeAsync(
                BaseUri,
                Credential,
                TestContext.Current.CancellationToken);
        AccountHealthProbeResult truncatedResult =
            await truncated.ProbeAsync(
                BaseUri,
                Credential,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            oversizedResult.Outcome);
        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            truncatedResult.Outcome);
    }

    [Theory]
    [InlineData(401, AccountHealthProbeOutcome.AuthenticationFailure, 401)]
    [InlineData(403, AccountHealthProbeOutcome.AuthenticationFailure, 403)]
    [InlineData(408, AccountHealthProbeOutcome.TransientFailure, 408)]
    [InlineData(404, AccountHealthProbeOutcome.Ignored, 404)]
    [InlineData(422, AccountHealthProbeOutcome.Ignored, 422)]
    [InlineData(301, AccountHealthProbeOutcome.TransientFailure, 301)]
    [InlineData(201, AccountHealthProbeOutcome.TransientFailure, 201)]
    [InlineData(500, AccountHealthProbeOutcome.TransientFailure, 500)]
    [InlineData(599, AccountHealthProbeOutcome.TransientFailure, 599)]
    [InlineData(600, AccountHealthProbeOutcome.TransientFailure, null)]
    public async Task HttpOutcomeMatrixIsNormalizedWithoutFollowingRedirects(
        int status,
        AccountHealthProbeOutcome expected,
        int? expectedStatus)
    {
        int calls = 0;
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(
                    (HttpStatusCode)status)
                {
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("body is never reported")),
                });
            });

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(expectedStatus, result.UpstreamStatusCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(
            "body is never reported",
            result.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task KnownStatusSurvivesOversizedOrFaultedBoundedDrain()
    {
        AccountHealthProbeHttpTransport oversized = Transport(
            (_, _) =>
            {
                StreamContent content = new(
                    new ThrowOnReadStream(new IOException("not read")));
                content.Headers.ContentLength = 65;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.Unauthorized)
                {
                    Content = content,
                });
            },
            maximumResponseBytes: 64);
        AccountHealthProbeHttpTransport faulted = Transport(
            (_, _) => Task.FromResult(
                StreamResponse(
                    HttpStatusCode.Forbidden,
                    new ThrowOnReadStream(new IOException("drain failed")))),
            maximumResponseBytes: 64);

        AccountHealthProbeResult oversizedResult =
            await oversized.ProbeAsync(
                BaseUri,
                Credential,
                TestContext.Current.CancellationToken);
        AccountHealthProbeResult faultedResult =
            await faulted.ProbeAsync(
                BaseUri,
                Credential,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountHealthProbeOutcome.AuthenticationFailure,
            oversizedResult.Outcome);
        Assert.Equal(
            AccountHealthProbeOutcome.AuthenticationFailure,
            faultedResult.Outcome);
    }

    [Fact]
    public async Task BoundedDrainStopsAfterMaximumPlusOneBytes()
    {
        CountingStream stream = new(new byte[128]);
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) => Task.FromResult(
                StreamResponse(HttpStatusCode.BadRequest, stream)),
            maximumResponseBytes: 64);

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountHealthProbeOutcome.Ignored, result.Outcome);
        Assert.Equal(65, stream.BytesRead);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("0", 1, null)]
    [InlineData("86401", 86400, null)]
    [InlineData("15.0", null, null)]
    [InlineData("Fri, 31 Jul 2026 08:32:00 GMT", null, "2026-07-31T08:32:00Z")]
    public async Task RetryAfterAcceptsOnlyStrictDeltaOrImfFixdate(
        string? raw,
        int? expectedSeconds,
        string? expectedAt)
    {
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) =>
            {
                HttpResponseMessage response = new(
                    HttpStatusCode.TooManyRequests)
                {
                    Content = new ByteArrayContent([]),
                };
                if (raw is not null)
                {
                    Assert.True(response.Headers.TryAddWithoutValidation(
                        "Retry-After",
                        raw));
                }

                return Task.FromResult(response);
            });

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountHealthProbeOutcome.RateLimited, result.Outcome);
        Assert.Equal(
            expectedSeconds is null
                ? null
                : TimeSpan.FromSeconds(expectedSeconds.Value),
            result.RetryAfter);
        Assert.Equal(
            expectedAt is null
                ? null
                : DateTimeOffset.Parse(
                    expectedAt,
                    System.Globalization.CultureInfo.InvariantCulture),
            result.RetryAfterAt);
    }

    [Fact]
    public async Task TransportFailuresAndDeadlineBecomeTransientFailure()
    {
        AccountHealthProbeHttpTransport httpFailure = Transport(
            (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("sensitive remote detail")));
        AccountHealthProbeHttpTransport socketFailure = Transport(
            (_, _) => Task.FromException<HttpResponseMessage>(
                new SocketException((int)SocketError.ConnectionRefused)));
        AccountHealthProbeHttpTransport timeout = Transport(
            async (_, cancellationToken) =>
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            },
            timeout: TimeSpan.FromMilliseconds(20));

        AccountHealthProbeResult httpResult = await httpFailure.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);
        AccountHealthProbeResult socketResult = await socketFailure.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);
        AccountHealthProbeResult timeoutResult = await timeout.ProbeAsync(
            BaseUri,
            Credential,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            httpResult.Outcome);
        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            socketResult.Outcome);
        Assert.Equal(
            AccountHealthProbeOutcome.TransientFailure,
            timeoutResult.Outcome);
        Assert.DoesNotContain(
            "sensitive remote detail",
            httpResult.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedDuringSendAndBodyRead()
    {
        using CancellationTokenSource sendCancellation = new();
        AccountHealthProbeHttpTransport duringSend = Transport(
            async (_, cancellationToken) =>
            {
                sendCancellation.Cancel();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("unreachable");
            });
        using CancellationTokenSource readCancellation = new();
        AccountHealthProbeHttpTransport duringRead = Transport(
            (_, _) =>
            {
                readCancellation.Cancel();
                return Task.FromResult(
                    StreamResponse(
                        HttpStatusCode.OK,
                        new CancellationStream()));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await duringSend.ProbeAsync(
                BaseUri,
                Credential,
                sendCancellation.Token).ConfigureAwait(false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await duringRead.ProbeAsync(
                BaseUri,
                Credential,
                readCancellation.Token).ConfigureAwait(false));
    }

    [Fact]
    public async Task InvalidCredentialIsIgnoredWithoutSendingARequest()
    {
        int calls = 0;
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"data":[]}"""));
            });

        AccountHealthProbeResult result = await transport.ProbeAsync(
            BaseUri,
            Encoding.UTF8.GetBytes("invalid\r\ncredential"),
            TestContext.Current.CancellationToken);

        Assert.Equal(AccountHealthProbeOutcome.Ignored, result.Outcome);
        Assert.Equal(0, calls);
        Assert.Null(result.UpstreamStatusCode);
    }

    [Fact]
    public async Task InvalidUtf8CredentialIsRejectedBeforeAnyHttpAuthorityExists()
    {
        int calls = 0;
        AccountHealthProbeHttpTransport transport = Transport(
            (_, _) =>
            {
                calls++;
                return Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"data":[]}"""));
            });

        await Assert.ThrowsAsync<DecoderFallbackException>(async () =>
            await transport.ProbeAsync(
                    BaseUri,
                    [0xff],
                    TestContext.Current.CancellationToken)
                .ConfigureAwait(false));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TransportAndPrimaryHandlerRejectMissingDependencies()
    {
        AccountHealthProbeHttpOptions options = Options();
        FakeTimeProvider time = new(ObservationTime);
        IHttpClientFactory factory = new SingleClientFactory(
            new StubHttpMessageHandler(
                (_, _) => Task.FromResult(JsonResponse(
                    HttpStatusCode.OK,
                    """{"data":[]}"""))));

        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeHttpTransport(null!, time, factory));
        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeHttpTransport(options, null!, factory));
        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeHttpTransport(options, time, null!));
        Assert.Throws<ArgumentNullException>(() =>
            AccountHealthProbeHttpTransport.CreatePrimaryHandler(null!));
    }

    [Fact]
    public async Task ExecutorReturnsNotFoundWithoutUnprotectingCredential()
    {
        ScriptedSnapshotReader snapshots = new(null, isCurrent: true);
        RecordingCredentialProtector protector = new();
        AccountHealthProbeExecutor executor = new(
            snapshots,
            protector,
            SuccessTransport());

        Result<AccountHealthProbeResult> result = await executor.ProbeAsync(
            AccountId(),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("not_found", result.Error.Code);
        Assert.Equal(0, protector.UnprotectCalls);
        Assert.Equal(0, snapshots.IsCurrentCalls);
    }

    [Fact]
    public async Task ExecutorFencesObservationAndZeroesCredentialLease()
    {
        AccountHealthProbeSnapshot snapshot = Snapshot();
        ScriptedSnapshotReader snapshots = new(snapshot, isCurrent: true);
        RecordingCredentialProtector protector = new();
        AccountHealthProbeExecutor executor = new(
            snapshots,
            protector,
            SuccessTransport());

        Result<AccountHealthProbeResult> result = await executor.ProbeAsync(
            snapshot.AccountId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.ExpectedAccountVersion);
        Assert.Equal(4, result.Value.ExpectedCredentialRevision);
        Assert.Equal(AccountHealthProbeOutcome.Success, result.Value.Outcome);
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Equal(snapshot.AccountId, protector.LastAccountId);
        Assert.Equal(1, snapshots.IsCurrentCalls);
        Assert.All(protector.LeasedBytes, value => Assert.Equal(0, value));
        Assert.DoesNotContain(
            "test-health-credential",
            result.Value.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutorRejectsStaleObservationAndStillZeroesCredentialLease()
    {
        AccountHealthProbeSnapshot snapshot = Snapshot();
        ScriptedSnapshotReader snapshots = new(snapshot, isCurrent: false);
        RecordingCredentialProtector protector = new();
        AccountHealthProbeExecutor executor = new(
            snapshots,
            protector,
            SuccessTransport());

        Result<AccountHealthProbeResult> result = await executor.ProbeAsync(
            snapshot.AccountId,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal("resource_conflict", result.Error.Code);
        Assert.Equal(1, snapshots.IsCurrentCalls);
        Assert.All(protector.LeasedBytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ExecutorDisposesCredentialWhenTransportFailsUnexpectedly()
    {
        AccountHealthProbeSnapshot snapshot = Snapshot();
        ScriptedSnapshotReader snapshots = new(snapshot, isCurrent: true);
        RecordingCredentialProtector protector = new();
        AccountHealthProbeExecutor executor = new(
            snapshots,
            protector,
            Transport(
                (_, _) => Task.FromException<HttpResponseMessage>(
                    new InvalidOperationException("programming fault"))));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.ProbeAsync(
                snapshot.AccountId,
                TestContext.Current.CancellationToken).ConfigureAwait(false));

        Assert.Equal(0, snapshots.IsCurrentCalls);
        Assert.All(protector.LeasedBytes, value => Assert.Equal(0, value));
    }

    [Fact]
    public void ExecutorRejectsMissingDependencies()
    {
        ScriptedSnapshotReader snapshots = new(Snapshot(), isCurrent: true);
        RecordingCredentialProtector protector = new();
        AccountHealthProbeHttpTransport transport = SuccessTransport();

        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeExecutor(null!, protector, transport));
        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeExecutor(snapshots, null!, transport));
        Assert.Throws<ArgumentNullException>(() =>
            new AccountHealthProbeExecutor(snapshots, protector, null!));
    }

    private static AccountHealthProbeHttpTransport SuccessTransport() =>
        Transport(
            (_, _) => Task.FromResult(JsonResponse(
                HttpStatusCode.OK,
                """{"data":[]}""")));

    private static AccountHealthProbeHttpTransport Transport(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            response,
        int maximumResponseBytes = 1_048_576,
        TimeSpan? timeout = null) =>
        Transport(
            new StubHttpMessageHandler(response),
            new(
                timeout ?? TimeSpan.FromSeconds(10),
                maximumResponseBytes,
                AllowLoopbackHttp: false));

    private static AccountHealthProbeHttpTransport Transport(
        HttpMessageHandler handler,
        AccountHealthProbeHttpOptions options) =>
        new(
            options,
            new FakeTimeProvider(ObservationTime),
            new SingleClientFactory(handler));

    private static AccountHealthProbeHttpOptions Options() =>
        new(
            Timeout: TimeSpan.FromSeconds(10),
            MaximumResponseBytes: 1_048_576,
            AllowLoopbackHttp: false);

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string body) =>
        new(status)
        {
            Content = new StringContent(
                body,
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage StreamResponse(
        HttpStatusCode status,
        Stream stream) =>
        new(status)
        {
            Content = new StreamContent(stream),
        };

    private static EntityId AccountId() =>
        new(Guid.Parse("018f3a4b-5c6d-7e8f-9012-3456789abcde"));

    private static AccountHealthProbeSnapshot Snapshot() =>
        new(
            AccountId(),
            BaseUri,
            CredentialRevision: 4,
            JsonSerializer.SerializeToElement(new
            {
                ciphertext = "opaque",
            }),
            AccountVersion: 7,
            Lifecycle: "active");

    private static async Task<string> ServeSingleResponseAsync(
        Socket listener,
        CancellationToken cancellationToken)
    {
        using Socket accepted = await listener
            .AcceptAsync(cancellationToken)
            .ConfigureAwait(false);
        using NetworkStream stream = new(accepted, ownsSocket: false);
        byte[] buffer = new byte[4096];
        int written = 0;
        while (written < buffer.Length)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(written),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (Encoding.ASCII.GetString(buffer, 0, written)
                .Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n"
            + "Content-Type: application/json\r\n"
            + "Content-Length: 11\r\n"
            + "Connection: close\r\n"
            + "\r\n"
            + "{\"data\":[]}");
        await stream
            .WriteAsync(response, cancellationToken)
            .ConfigureAwait(false);
        return Encoding.ASCII.GetString(buffer, 0, written);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>
            response) : HttpMessageHandler
    {
        private readonly Func<
            HttpRequestMessage,
            CancellationToken,
            Task<HttpResponseMessage>> _response =
                response ?? throw new ArgumentNullException(nameof(response));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _response(request, cancellationToken);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly IHttpClientFactory _factory;

        internal SingleClientFactory(HttpMessageHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ServiceCollection services = new();
            services
                .AddHttpClient(AccountHealthProbeHttpTransport.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            IServiceProvider provider = services.BuildServiceProvider();
            _factory = provider.GetRequiredService<IHttpClientFactory>();
        }

        public HttpClient CreateClient(string name)
        {
            Assert.Equal(AccountHealthProbeHttpTransport.ClientName, name);
            return _factory.CreateClient(name);
        }
    }

    private sealed class CapturedRequest
    {
        internal HttpMethod? Method { get; set; }

        internal Uri? Uri { get; set; }

        internal string? Authority { get; set; }

        internal string? AuthorizationScheme { get; set; }

        internal string? AuthorizationParameter { get; set; }

        internal string[] Accept { get; set; } = [];

        internal bool? ConnectionClose { get; set; }
    }

    private sealed class ThrowOnReadStream(Exception exception) : Stream
    {
        internal int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw exception;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromException<int>(exception);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class PrefixThenThrowStream(
        byte[] prefix,
        Exception exception) : Stream
    {
        private bool _returnedPrefix;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_returnedPrefix)
            {
                _returnedPrefix = true;
                prefix.CopyTo(buffer);
                return ValueTask.FromResult(prefix.Length);
            }

            return ValueTask.FromException<int>(exception);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class CountingStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);

        internal int BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            int read = await _inner
                .ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .ConfigureAwait(false);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ValueTask<int> read = _inner.ReadAsync(
                buffer,
                cancellationToken);
            if (read.IsCompletedSuccessfully)
            {
                BytesRead += read.Result;
                return read;
            }

            return AwaitAndCountAsync(read);
        }

        private async ValueTask<int> AwaitAndCountAsync(ValueTask<int> read)
        {
            int result = await read.ConfigureAwait(false);
            BytesRead += result;
            return result;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancellationStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedSnapshotReader(
        AccountHealthProbeSnapshot? snapshot,
        bool isCurrent) : IAccountHealthProbeSnapshotReader
    {
        internal int IsCurrentCalls { get; private set; }

        public ValueTask<AccountHealthProbeSnapshot?> ReadAsync(
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<bool> IsCurrentAsync(
            AccountHealthProbeSnapshot observed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsCurrentCalls++;
            Assert.Same(snapshot, observed);
            return ValueTask.FromResult(isCurrent);
        }
    }

    private sealed class RecordingCredentialProtector : IAccountCredentialProtector
    {
        internal byte[] LeasedBytes { get; } =
            Encoding.UTF8.GetBytes("test-health-credential");

        internal int UnprotectCalls { get; private set; }

        internal EntityId? LastAccountId { get; private set; }

        public AccountCredentialProtection Protect(
            string credential,
            EntityId accountId) =>
            throw new InvalidOperationException("Not used by health probes.");

        public ValueTask<AccountCredentialLease> UnprotectAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnprotectCalls++;
            LastAccountId = accountId;
            return ValueTask.FromResult(
                new AccountCredentialLease(LeasedBytes));
        }

        public ValueTask<AccountCredentialRewrap> RewrapAsync(
            JsonElement envelope,
            EntityId accountId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Not used by health probes.");
    }
}
