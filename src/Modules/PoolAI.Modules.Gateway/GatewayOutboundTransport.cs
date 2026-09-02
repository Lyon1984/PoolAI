using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using OpenTelemetry;
using PoolAI.BuildingBlocks;
using PoolAI.Contracts.Generated;
using PoolAI.Modules.Gateway.Abstractions;

namespace PoolAI.Modules.Gateway.Application;

internal sealed class GatewayOutboundTransport(
    GatewayOutboundTransportOptions options,
    IGatewayDnsResolver dnsResolver,
    TimeProvider timeProvider) : IGatewayUpstreamTransport
{
    private static readonly TimeSpan MaximumTimerDelay =
        TimeSpan.FromMilliseconds(uint.MaxValue - 1u);
    private readonly GatewayOutboundTransportOptions _options = options
        ?? throw new ArgumentNullException(nameof(options));
    private readonly IGatewayDnsResolver _dnsResolver = dnsResolver
        ?? throw new ArgumentNullException(nameof(dnsResolver));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly GatewayAuthorityConcurrencyLimiter _authorityLimiter =
        new((options ?? throw new ArgumentNullException(nameof(options)))
            .MaxConnectionsPerServer);

    public async ValueTask<GatewayUpstreamTransportResult> SendAsync(
        IPreparedUpstreamAttempt preparedAttempt,
        AdapterAttemptContext attemptContext,
        AdapterCapability capability,
        IUpstreamCredentialHandle credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedAttempt);
        ArgumentNullException.ThrowIfNull(attemptContext);
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(credential);
        if (attemptContext.Phase
            < GatewayAttemptPhase.DispatchedNoDownstreamHeaders)
        {
            throw new InvalidOperationException(
                "The dispatch fence must commit before transport send.");
        }

        TimeSpan remaining = attemptContext.Deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Failure(
                ErrorCodesV1.UpstreamUnavailable,
                "The upstream request deadline expired before transport send.",
                GatewayRequestWriteEvidence.ConfirmedNotWritten,
                capability);
        }

        using CancellationTokenSource deadlineCancellation = new(
            remaining <= MaximumTimerDelay ? remaining : MaximumTimerDelay,
            _timeProvider);
        using CancellationTokenSource attemptCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadlineCancellation.Token);

        Result<PreparedUpstreamRequest> created = await CreatePreparedAsync(
                preparedAttempt,
                attemptCancellation.Token)
            .ConfigureAwait(false);

        if (created.IsFailure)
        {
            return new(
                CopyFailure<NormalizedUpstreamResult>(created.Error),
                GatewayRequestWriteEvidence.ConfirmedNotWritten,
                capability.CanProveNoRequestBytesWritten);
        }

        using PreparedUpstreamRequest prepared = created.Value;
        return await SendPreparedAsync(
                preparedAttempt,
                attemptContext,
                capability,
                credential,
                prepared,
                attemptCancellation.Token)
            .ConfigureAwait(false);
    }

    private static async ValueTask<Result<PreparedUpstreamRequest>>
        CreatePreparedAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            CancellationToken cancellationToken)
    {
        try
        {
            return await preparedAttempt.CreateRequestAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return Result.Failure<PreparedUpstreamRequest>(
                ErrorCodesV1.UpstreamUnavailable,
                "The upstream request was cancelled before transport send.");
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or InvalidOperationException)
        {
            return Result.Failure<PreparedUpstreamRequest>(
                ErrorCodesV1.UpstreamProtocolError,
                "The Adapter could not prepare a valid upstream request.");
        }
        catch (Exception)
        {
            return Result.Failure<PreparedUpstreamRequest>(
                ErrorCodesV1.UpstreamProtocolError,
                "The Adapter could not prepare a valid upstream request.");
        }
    }

    private async ValueTask<GatewayUpstreamTransportResult> SendPreparedAsync(
        IPreparedUpstreamAttempt preparedAttempt,
        AdapterAttemptContext attemptContext,
        AdapterCapability capability,
        IUpstreamCredentialHandle credential,
        PreparedUpstreamRequest prepared,
        CancellationToken cancellationToken)
    {
        if (!IsBoundDestination(
                prepared.RequestUri,
                attemptContext.Route.UpstreamBaseUri)
            || credential is not ITransportCredentialHandle transportCredential)
        {
            return Failure(
                ErrorCodesV1.UpstreamProtocolError,
                "The prepared upstream destination is not bound to the selected route.",
                GatewayRequestWriteEvidence.ConfirmedNotWritten,
                capability);
        }

        HttpRequestMessage request;
        try
        {
            request = CreateTransportRequest(prepared);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FormatException
                or InvalidOperationException)
        {
            return Failure(
                ErrorCodesV1.UpstreamProtocolError,
                "The prepared upstream request is invalid.",
                GatewayRequestWriteEvidence.ConfirmedNotWritten,
                capability);
        }

        using (request)
        {
            return await SendTransportOwnedRequestAsync(
                    preparedAttempt,
                    capability,
                    transportCredential,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<GatewayUpstreamTransportResult>
        SendTransportOwnedRequestAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            AdapterCapability capability,
            ITransportCredentialHandle credential,
            HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SendState state = new(
            request,
            request.RequestUri!,
            credential);
        try
        {
            return await SendWithinAuthorityLimitAsync(
                    preparedAttempt,
                    capability,
                    request,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                ErrorCodesV1.UpstreamUnavailable,
                "The upstream request was cancelled or timed out.",
                state.WriteEvidence,
                capability);
        }
        catch (Exception exception) when (
            state.WriteEvidence == GatewayRequestWriteEvidence.ConfirmedWritten
            && exception is FormatException or InvalidOperationException)
        {
            return Failure(
                ErrorCodesV1.UpstreamProtocolError,
                "The upstream response could not be parsed safely.",
                state.WriteEvidence,
                capability);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or SocketException
                or AuthenticationException)
        {
            return Failure(
                ErrorCodesV1.UpstreamUnavailable,
                "The upstream connection or request failed.",
                state.WriteEvidence,
                capability);
        }
        catch (Exception)
        {
            return Failure(
                ErrorCodesV1.UpstreamProtocolError,
                "The upstream response could not be parsed safely.",
                state.WriteEvidence,
                capability);
        }
        finally
        {
            state.ClearCredentialAttachment();
            request.Headers.Authorization = null;
        }
    }

    private async ValueTask<GatewayUpstreamTransportResult>
        SendWithinAuthorityLimitAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            AdapterCapability capability,
            HttpRequestMessage request,
            SendState state,
            CancellationToken cancellationToken)
    {
        using IDisposable authorityLease = await _authorityLimiter
            .AcquireAsync(request.RequestUri!, cancellationToken)
            .ConfigureAwait(false);
        using SocketsHttpHandler handler = CreatePrimaryHandler(
            _options,
            _dnsResolver,
            state);
        using HttpMessageInvoker client = new(handler, disposeHandler: false);
        using HttpResponseMessage response = await SendForFirstByteAsync(
                client,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return await ParseTransportResponseAsync(
                preparedAttempt,
                capability,
                request,
                response,
                state,
                _options.StreamIdleTimeout,
                _timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<HttpResponseMessage> SendForFirstByteAsync(
        HttpMessageInvoker client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource firstByteTimeout = new(
            _options.FirstByteTimeout,
            _timeProvider);
        using CancellationTokenSource firstByteCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                firstByteTimeout.Token);
        using IDisposable instrumentation = SuppressInstrumentationScope.Begin();
        return await client.SendAsync(request, firstByteCancellation.Token)
            .ConfigureAwait(false);
    }

    private static async ValueTask<GatewayUpstreamTransportResult>
        ParseTransportResponseAsync(
            IPreparedUpstreamAttempt preparedAttempt,
            AdapterCapability capability,
            HttpRequestMessage request,
            HttpResponseMessage response,
            SendState state,
            TimeSpan streamIdleTimeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        state.MarkConfirmedWritten();
        int statusCode = checked((int)response.StatusCode);
        response.RequestMessage = null;
        request.Headers.Authorization = null;
        Stream content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using Stream boundedContent = new StreamIdleTimeoutStream(
            content,
            streamIdleTimeout,
            timeProvider);
        AdapterUpstreamResponse adapterResponse = new(
            statusCode,
            boundedContent,
            ResponseHeaders(response));
        Result<NormalizedUpstreamResult> parsed = await preparedAttempt
            .ParseResponseAsync(adapterResponse, cancellationToken)
            .ConfigureAwait(false);
        if (parsed.IsSuccess)
        {
            parsed = Result.Success(parsed.Value with
            {
                StatusCode = statusCode,
            });
        }

        bool confirmedNoExecution = parsed.IsSuccess
            && capability.ConfirmsNoExecutionForStatus(statusCode)
            && parsed.Value.Usage is null;
        return new(parsed, state.WriteEvidence, confirmedNoExecution);
    }

    internal static SocketsHttpHandler CreatePrimaryHandler(
        GatewayOutboundTransportOptions options,
        IGatewayDnsResolver dnsResolver,
        SendState state)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dnsResolver);
        ArgumentNullException.ThrowIfNull(state);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(
                    context,
                    options,
                    dnsResolver,
                    state,
                    cancellationToken),
            ConnectTimeout = options.ConnectTimeout,
            Credentials = null,
            EnableMultipleHttp2Connections = false,
            MaxConnectionsPerServer = options.MaxConnectionsPerServer,
            PlaintextStreamFilter = (context, _) =>
                state.AuthorizeValidatedConnection(context),
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            PooledConnectionLifetime = TimeSpan.Zero,
            PreAuthenticate = false,
            Proxy = null,
            UseCookies = false,
            UseProxy = false,
        };
    }

    private static HttpRequestMessage CreateTransportRequest(
        PreparedUpstreamRequest prepared)
    {
        HttpRequestMessage request = new(prepared.Method, prepared.RequestUri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        try
        {
            request.Headers.ConnectionClose = true;
            foreach (PreparedUpstreamHeader header in prepared.Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(
                        header.Name,
                        header.Value))
                {
                    throw new InvalidOperationException(
                        "The prepared upstream header could not be applied.");
                }
            }

            if (!prepared.Body.IsEmpty)
            {
                ZeroizingByteArrayContent content = new(prepared.Body.Span);
                content.Headers.ContentType = new MediaTypeHeaderValue(
                    "application/json");
                request.Content = content;
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>>
        ResponseHeaders(HttpResponseMessage response) =>
        response.Headers
            .Concat(response.Content.Headers)
            .Select(static header =>
                new KeyValuePair<string, IEnumerable<string>>(
                    header.Key,
                    header.Value));

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        GatewayOutboundTransportOptions options,
        IGatewayDnsResolver dnsResolver,
        SendState state,
        CancellationToken cancellationToken)
    {
        state.BeginConnection();
        Uri requestUri = context.InitialRequestMessage.RequestUri
            ?? throw new HttpRequestException(
                "The upstream request URI is missing.");
        if (!ReferenceEquals(context.InitialRequestMessage, state.Request)
            || !IsBoundDnsEndpoint(requestUri, context.DnsEndPoint))
        {
            throw new HttpRequestException(
                "The transport connection target is inconsistent.");
        }

        IPAddress[] addresses = await dnsResolver.ResolveAsync(
                context.DnsEndPoint.Host,
                cancellationToken)
            .ConfigureAwait(false);
        if (!GatewayUpstreamAddressClassifier.AreAllAllowed(
                requestUri,
                addresses,
                options))
        {
            throw new HttpRequestException(
                "The upstream address is outside the egress policy.");
        }

        IPAddress selected = GatewayIpCidr.NormalizeMappedAddress(addresses[0]);
        Socket socket = new(
            selected.AddressFamily,
            SocketType.Stream,
            ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(selected, context.DnsEndPoint.Port),
                cancellationToken).ConfigureAwait(false);
            state.MarkDirectConnectionEstablished();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool IsBoundDnsEndpoint(
        Uri requestUri,
        DnsEndPoint endpoint) =>
        requestUri.Port == endpoint.Port
        && string.Equals(
            NormalizeHost(requestUri),
            NormalizeHost(endpoint.Host),
            StringComparison.Ordinal);

    private static bool IsBoundDestination(Uri requestUri, Uri routeBaseUri) =>
        requestUri.IsAbsoluteUri
        && routeBaseUri.IsAbsoluteUri
        && string.Equals(
            CanonicalAuthority(requestUri),
            CanonicalAuthority(routeBaseUri),
            StringComparison.Ordinal);

    internal static string CanonicalAuthority(Uri uri)
    {
        string host = NormalizeHost(uri);
        string authorityHost = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{host}]"
            : host;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{uri.Scheme.ToLowerInvariant()}://{authorityHost}:{uri.Port}");
    }

    private static string NormalizeHost(Uri uri) => NormalizeHost(
        uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost
            : uri.DnsSafeHost);

    private static string NormalizeHost(string host)
    {
        string normalized = host;
        if (normalized.Length >= 2
            && normalized[0] == '['
            && normalized[^1] == ']')
        {
            normalized = normalized[1..^1];
        }

        return normalized.ToLowerInvariant();
    }

    private static GatewayUpstreamTransportResult Failure(
        string code,
        string description,
        GatewayRequestWriteEvidence evidence,
        AdapterCapability capability) => new(
        Result.Failure<NormalizedUpstreamResult>(code, description),
        evidence,
        capability.CanProveNoRequestBytesWritten
            && evidence == GatewayRequestWriteEvidence.ConfirmedNotWritten);

    private static Result<T> CopyFailure<T>(ResultError error) =>
        Result.Failure<T>(
            error.Code,
            error.Description,
            error.RetryAfterSeconds,
            error.ETag,
            error.Presentation);

    internal sealed class SendState(
        HttpRequestMessage request,
        Uri destination,
        ITransportCredentialHandle credential)
    {
        private int _connectionStarted;
        private int _directConnectionEstablished;
        private int _validatedConnectionAuthorized;
        private int _writeEvidence;
        private ITransportCredentialAttachment? _credentialAttachment;

        internal HttpRequestMessage Request { get; } = request
            ?? throw new ArgumentNullException(nameof(request));

        internal GatewayRequestWriteEvidence WriteEvidence =>
            (GatewayRequestWriteEvidence)Volatile.Read(ref _writeEvidence);

        internal void BeginConnection()
        {
            if (Interlocked.Exchange(ref _connectionStarted, 1) != 0)
            {
                throw new HttpRequestException(
                    "The single-attempt transport cannot open a second connection.");
            }
        }

        internal void MarkDirectConnectionEstablished() =>
            Interlocked.Exchange(ref _directConnectionEstablished, 1);

        internal ValueTask<Stream> AuthorizeValidatedConnection(
            SocketsHttpPlaintextStreamFilterContext context)
        {
            if (!ReferenceEquals(context.InitialRequestMessage, Request)
                || Volatile.Read(ref _directConnectionEstablished) == 0
                || Interlocked.Exchange(
                    ref _validatedConnectionAuthorized,
                    1) != 0)
            {
                throw new HttpRequestException(
                    "The validated upstream connection is inconsistent.");
            }

            // For HTTPS this callback is invoked only after the handler has
            // authenticated TLS with the original URI host. The request is
            // transport-owned, so the Adapter never observes this header.
            ITransportCredentialAttachment attachment =
                credential.AttachAuthorizationOnce(destination, Request);
            if (Interlocked.CompareExchange(
                    ref _credentialAttachment,
                    attachment,
                    null) is not null)
            {
                attachment.Dispose();
                throw new HttpRequestException(
                    "The validated connection already owns a credential attachment.");
            }

            return ValueTask.FromResult<Stream>(
                new RequestWriteObservingStream(
                    context.PlaintextStream,
                    MarkPossiblyWritten));
        }

        internal void ClearCredentialAttachment()
        {
            ITransportCredentialAttachment? attachment = Interlocked.Exchange(
                ref _credentialAttachment,
                null);
            attachment?.Dispose();
            Request.Headers.Authorization = null;
        }

        internal void MarkConfirmedWritten() => Interlocked.Exchange(
            ref _writeEvidence,
            (int)GatewayRequestWriteEvidence.ConfirmedWritten);

        private void MarkPossiblyWritten() => Interlocked.CompareExchange(
            ref _writeEvidence,
            (int)GatewayRequestWriteEvidence.PossiblyWritten,
            (int)GatewayRequestWriteEvidence.ConfirmedNotWritten);
    }

    private sealed class StreamIdleTimeoutStream(
        Stream inner,
        TimeSpan idleTimeout,
        TimeProvider timeProvider) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override int Read(Span<byte> buffer)
        {
            byte[] temporary = new byte[buffer.Length];
            try
            {
                int read = ReadAsync(
                        temporary,
                        0,
                        temporary.Length,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                temporary.AsSpan(0, read).CopyTo(buffer);
                return read;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(temporary);
            }
        }

        public override int ReadByte()
        {
            Span<byte> value = stackalloc byte[1];
            return Read(value) == 0 ? -1 : value[0];
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => ReadWithTimeoutAsync(
                buffer.AsMemory(offset, count),
                cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ReadWithTimeoutAsync(buffer, cancellationToken);

        private async ValueTask<int> ReadWithTimeoutAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeoutCancellation = new(
                idleTimeout,
                timeProvider);
            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCancellation.Token);
            try
            {
                return await inner.ReadAsync(
                        buffer,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (timeoutCancellation.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
            {
                throw new IOException(
                    "The upstream response stream exceeded its idle timeout.",
                    exception);
            }
        }

        public override void Flush() => throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

    }

    private sealed class RequestWriteObservingStream(
        Stream inner,
        Action beforeWrite) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            beforeWrite();
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            beforeWrite();
            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            beforeWrite();
            return inner.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            beforeWrite();
            inner.WriteByte(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

    }

    private sealed class ZeroizingByteArrayContent : ByteArrayContent
    {
        private readonly byte[] _buffer;

        internal ZeroizingByteArrayContent(ReadOnlySpan<byte> content)
            : this(content.ToArray())
        {
        }

        private ZeroizingByteArrayContent(byte[] content)
            : base(content)
        {
            _buffer = content;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CryptographicOperations.ZeroMemory(_buffer);
            }

            base.Dispose(disposing);
        }
    }
}
