using System.Security.Cryptography;

namespace PoolAI.Modules.Gateway.Abstractions;

public sealed class PreparedUpstreamRequest : IDisposable
{
    private byte[]? _body;

    public PreparedUpstreamRequest(
        HttpMethod method,
        Uri requestUri,
        ReadOnlySpan<byte> body,
        IEnumerable<PreparedUpstreamHeader>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(requestUri);
        if (method != HttpMethod.Get && method != HttpMethod.Post)
        {
            throw new ArgumentException(
                "Only GET and POST upstream requests are supported.",
                nameof(method));
        }

        if (!requestUri.IsAbsoluteUri
            || requestUri.HostNameType is UriHostNameType.Unknown
                or UriHostNameType.Basic
            || !string.Equals(
                requestUri.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.Ordinal)
                && !string.Equals(
                    requestUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.Ordinal)
            || !string.IsNullOrEmpty(requestUri.UserInfo)
            || !string.IsNullOrEmpty(requestUri.Fragment)
            || requestUri.Port is < 1 or > 65_535
            || method == HttpMethod.Get && !body.IsEmpty)
        {
            throw new ArgumentException(
                "The prepared upstream request target is invalid.",
                nameof(requestUri));
        }

        PreparedUpstreamHeader[] copiedHeaders = headers?.ToArray() ?? [];
        if (copiedHeaders.Length > 64
            || copiedHeaders.Any(static header => header is null)
            || copiedHeaders
                .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
                .Any(static group => group.Count() != 1))
        {
            throw new ArgumentException(
                "The prepared upstream request headers are invalid.",
                nameof(headers));
        }

        Method = method;
        RequestUri = requestUri;
        Headers = Array.AsReadOnly(copiedHeaders);
        _body = body.ToArray();
    }

    public HttpMethod Method { get; }

    public Uri RequestUri { get; }

    public IReadOnlyList<PreparedUpstreamHeader> Headers { get; }

    public ReadOnlyMemory<byte> Body => _body
        ?? throw new ObjectDisposedException(nameof(PreparedUpstreamRequest));

    public void Dispose()
    {
        byte[]? body = Interlocked.Exchange(ref _body, null);
        if (body is not null)
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    public override string ToString() => nameof(PreparedUpstreamRequest);
}
