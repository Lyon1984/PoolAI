namespace PoolAI.Modules.Gateway.Abstractions;

public sealed record PreparedUpstreamHeader
{
    private static readonly HashSet<string> ForbiddenNames = new(
        [
            "authorization",
            "connection",
            "content-length",
            "cookie",
            "host",
            "keep-alive",
            "proxy-authorization",
            "proxy-connection",
            "te",
            "trailer",
            "transfer-encoding",
            "upgrade",
        ],
        StringComparer.OrdinalIgnoreCase);

    public PreparedUpstreamHeader(string name, string value)
    {
        if (!IsValidName(name)
            || ForbiddenNames.Contains(name)
            || !IsValidValue(value))
        {
            throw new ArgumentException(
                "The prepared upstream header is invalid.",
                nameof(name));
        }

        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    public override string ToString() => nameof(PreparedUpstreamHeader);

    private static bool IsValidName(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*'
                or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private static bool IsValidValue(string? value) =>
        value is { Length: <= 8192 }
        && value.All(static character =>
            character == '\t' || character is >= ' ' and <= '~');
}
