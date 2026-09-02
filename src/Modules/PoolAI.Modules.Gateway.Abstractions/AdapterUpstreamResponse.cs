namespace PoolAI.Modules.Gateway.Abstractions;

public sealed class AdapterUpstreamResponse
{
    private readonly Dictionary<string, IReadOnlyList<string>> _headers;

    public AdapterUpstreamResponse(
        int statusCode,
        Stream content,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(headers);
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        Dictionary<string, IReadOnlyList<string>> copied =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            if (string.IsNullOrWhiteSpace(header.Key)
                || header.Value is null
                || !copied.TryAdd(
                    header.Key,
                    Array.AsReadOnly(header.Value.ToArray())))
            {
                throw new ArgumentException(
                    "The upstream response headers are invalid.",
                    nameof(headers));
            }
        }

        StatusCode = statusCode;
        Content = content;
        _headers = copied;
    }

    public int StatusCode { get; }

    public Stream Content { get; }

    public bool TryGetHeader(
        string name,
        out IReadOnlyList<string> values) =>
        _headers.TryGetValue(name, out values!);

    public override string ToString() => nameof(AdapterUpstreamResponse);
}
