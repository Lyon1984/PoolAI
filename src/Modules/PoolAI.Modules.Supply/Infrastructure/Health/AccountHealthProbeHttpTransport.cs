using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenTelemetry;
using PoolAI.Modules.Supply.Abstractions;

namespace PoolAI.Modules.Supply.Infrastructure.Health;

internal sealed class AccountHealthProbeHttpTransport
{
    internal const string ClientName = "PoolAI.SupplyHealthProbe";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly AccountHealthProbeHttpOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IHttpClientFactory _clientFactory;

    public AccountHealthProbeHttpTransport(
        AccountHealthProbeHttpOptions options,
        TimeProvider timeProvider,
        IHttpClientFactory clientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _clientFactory = clientFactory
            ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    internal ValueTask<AccountHealthProbeResult> ProbeAsync(
        Uri baseUri,
        ReadOnlySpan<byte> utf8Credential,
        CancellationToken cancellationToken)
    {
        string credential = StrictUtf8.GetString(utf8Credential);
        return ProbeCoreAsync(baseUri, credential, cancellationToken);
    }

    private async ValueTask<AccountHealthProbeResult> ProbeCoreAsync(
        Uri baseUri,
        string credential,
        CancellationToken cancellationToken)
    {
        Uri requestUri = ModelsUri(baseUri);
        using HttpClient client = _clientFactory.CreateClient(ClientName);
        using HttpRequestMessage? request = CreateRequest(
            requestUri,
            credential);
        if (request is null)
        {
            return new(
                AccountHealthProbeOutcome.Ignored,
                RetryAfter: null,
                _timeProvider.GetUtcNow());
        }

        using CancellationTokenSource timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using IDisposable instrumentation =
                SuppressInstrumentationScope.Begin();
            using HttpResponseMessage response = await client
                .SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            DateTimeOffset observedAt = _timeProvider.GetUtcNow();
            return await ClassifyResponseAsync(
                response,
                observedAt,
                timeout.Token,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or SocketException
                or OperationCanceledException
                or JsonException
                or DecoderFallbackException)
        {
            return new(
                AccountHealthProbeOutcome.TransientFailure,
                RetryAfter: null,
                _timeProvider.GetUtcNow());
        }
    }

    private static HttpRequestMessage? CreateRequest(
        Uri requestUri,
        string credential)
    {
        HttpRequestMessage request = new(HttpMethod.Get, requestUri);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                credential);
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.ConnectionClose = true;
            return request;
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentException)
        {
            request.Dispose();
            return null;
        }
    }

    private async ValueTask<AccountHealthProbeResult> ClassifyResponseAsync(
        HttpResponseMessage response,
        DateTimeOffset observedAt,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        int status = checked((int)response.StatusCode);
        if (status == 200)
        {
            return await ClassifySuccessAsync(
                response.Content,
                observedAt,
                timeoutToken,
                callerToken).ConfigureAwait(false);
        }

        AccountHealthProbeResult classified = ClassifyStatus(
            response,
            observedAt,
            status);
        await DrainKnownResponseAsync(
            response.Content,
            timeoutToken,
            callerToken).ConfigureAwait(false);
        return classified;
    }

    private async ValueTask<AccountHealthProbeResult> ClassifySuccessAsync(
        HttpContent content,
        DateTimeOffset observedAt,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        bool valid;
        try
        {
            valid = await HasValidModelsDocumentAsync(
                content,
                timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or OperationCanceledException
                or JsonException
                or DecoderFallbackException)
        {
            valid = false;
        }

        return new(
            valid
                ? AccountHealthProbeOutcome.Success
                : AccountHealthProbeOutcome.TransientFailure,
            RetryAfter: null,
            observedAt,
            UpstreamStatusCode: 200);
    }

    private static AccountHealthProbeResult ClassifyStatus(
        HttpResponseMessage response,
        DateTimeOffset observedAt,
        int status)
    {
        if (status == 429)
        {
            ParsedRetryAfter retryAfter = ParseRetryAfter(response.Headers);
            return new(
                AccountHealthProbeOutcome.RateLimited,
                retryAfter.Delta,
                observedAt,
                status,
                RetryAfterAt: retryAfter.At);
        }

        return status switch
        {
            401 or 403 => new(
                AccountHealthProbeOutcome.AuthenticationFailure,
                RetryAfter: null,
                observedAt,
                status),
            408 => new(
                AccountHealthProbeOutcome.TransientFailure,
                RetryAfter: null,
                observedAt,
                status),
            >= 400 and <= 499 => new(
                AccountHealthProbeOutcome.Ignored,
                RetryAfter: null,
                observedAt,
                status),
            _ => new(
                AccountHealthProbeOutcome.TransientFailure,
                RetryAfter: null,
                observedAt,
                status is >= 200 and <= 399 or 408 or >= 500 and <= 599
                    ? status
                    : null),
        };
    }

    private async ValueTask DrainKnownResponseAsync(
        HttpContent content,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        try
        {
            await DrainBoundedAsync(content, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or OperationCanceledException)
        {
            // The response status remains attributable. ConnectionClose and
            // disposal prevent reuse after an incomplete bounded drain.
        }
    }

    private async ValueTask DrainBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            return;
        }

        Stream source = await content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sourceLease =
            source.ConfigureAwait(false);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int remaining = checked(_options.MaximumResponseBytes + 1);
            while (remaining > 0)
            {
                int read = await source.ReadAsync(
                    chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }
    }

    internal static SocketsHttpHandler CreatePrimaryHandler(
        AccountHealthProbeHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context, options, cancellationToken),
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            PooledConnectionLifetime = TimeSpan.Zero,
            UseCookies = false,
            UseProxy = false,
        };
    }

    private async ValueTask<bool> HasValidModelsDocumentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes)
        {
            return false;
        }

        Stream source = await content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable sourceLease =
            source.ConfigureAwait(false);
        ArrayBufferWriter<byte> buffer = new();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(
                    chunk,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.WrittenCount > _options.MaximumResponseBytes - read)
                {
                    return false;
                }

                buffer.Write(chunk.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk, clearArray: true);
        }

        using JsonDocument document = JsonDocument.Parse(
            buffer.WrittenMemory);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Array;
    }

    private static Uri ModelsUri(Uri baseUri)
    {
        string path = baseUri.AbsolutePath.TrimEnd('/');
        UriBuilder builder = new(baseUri)
        {
            Path = $"{path}/models",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static ParsedRetryAfter ParseRetryAfter(
        HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            return new(Delta: null, At: null);
        }

        string[] supplied = [.. values];
        if (supplied.Length != 1)
        {
            return new(Delta: null, At: null);
        }

        string value = supplied[0];
        if (value.Length > 0
            && value.All(static character => character is >= '0' and <= '9')
            && ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong seconds))
        {
            long boundedSeconds = checked((long)Math.Clamp(seconds, 1UL, 86_400UL));
            return new(TimeSpan.FromSeconds(boundedSeconds), At: null);
        }

        return DateTimeOffset.TryParseExact(
            value,
            "r",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset at)
            ? new(Delta: null, at)
            : new(Delta: null, At: null);
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        AccountHealthProbeHttpOptions options,
        CancellationToken cancellationToken)
    {
        Uri requestUri = context.InitialRequestMessage.RequestUri
            ?? throw new HttpRequestException(
                "The upstream health request URI is missing.");
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        if (!UpstreamAddressClassifier.AreAllAllowed(
                requestUri,
                addresses,
                options))
        {
            throw new HttpRequestException(
                "The upstream address is outside the egress policy.");
        }

        IPAddress selected = AccountHealthProbeHttpOptions
            .UpstreamPrivateEgressRule.CanonicalAddress(
                addresses[Random.Shared.Next(addresses.Length)]);
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
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed record ParsedRetryAfter(
        TimeSpan? Delta,
        DateTimeOffset? At);
}
