using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace PoolAI.Modules.Identity.Worker;

internal static class EmailHeaderValueValidator
{
    internal static string NormalizeMailbox(string value, string parameterName)
    {
        ValidateHeader(value, parameterName, 320);
        MailAddress address;
        try
        {
            address = new MailAddress(value);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Email mailbox is invalid.", parameterName, exception);
        }

        if (!string.Equals(address.Address, value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Email mailbox must not include a display name or surrounding whitespace.",
                parameterName);
        }

        int separator = address.Address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Address.Length - 1)
        {
            throw new ArgumentException("Email mailbox is invalid.", parameterName);
        }

        string localPart = address.Address[..separator];
        ValidateLocalPart(localPart, parameterName);
        string asciiDomain = NormalizeDomain(
            address.Address[(separator + 1)..],
            parameterName);

        string normalized = string.Concat(localPart, "@", asciiDomain.ToLowerInvariant());
        if (normalized.Length > 254)
        {
            throw new ArgumentException("Email mailbox is too long.", parameterName);
        }

        ValidateHeader(normalized, parameterName, 320);
        return normalized;
    }

    private static void ValidateLocalPart(string localPart, string parameterName)
    {
        if (localPart.Length is < 1 or > 64
            || localPart[0] == '.'
            || localPart[^1] == '.'
            || localPart.Contains("..", StringComparison.Ordinal)
            || !localPart.All(IsMailboxLocalPartCharacter))
        {
            throw new ArgumentException(
                "Email local part is not supported in Release 1.",
                parameterName);
        }
    }

    private static string NormalizeDomain(string domain, string parameterName)
    {
        string asciiDomain;
        try
        {
            asciiDomain = new IdnMapping
            {
                UseStd3AsciiRules = true,
            }.GetAscii(domain);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Email domain is invalid.", parameterName, exception);
        }

        asciiDomain = asciiDomain.ToLowerInvariant();
        if (!IsCanonicalDnsDomain(asciiDomain))
        {
            throw new ArgumentException("Email domain is invalid.", parameterName);
        }

        return asciiDomain;
    }

    internal static string ValidateDisplayName(string value, string parameterName)
    {
        ValidateHeader(value, parameterName, 128);
        return value;
    }

    internal static string ValidateSubject(string value, string parameterName)
    {
        ValidateHeader(value, parameterName, 200);
        return value;
    }

    internal static string ValidateMessageId(string value, string parameterName)
    {
        ValidateHeader(value, parameterName, 320);
        if (value.Length < 5 || value[0] != '<' || value[^1] != '>')
        {
            throw new ArgumentException("SMTP Message-ID is invalid.", parameterName);
        }

        ReadOnlySpan<char> content = value.AsSpan(1, value.Length - 2);
        int separator = content.IndexOf('@');
        string domain = separator >= 0 && separator < content.Length - 1
            ? content[(separator + 1)..].ToString()
            : string.Empty;
        string normalizedDomain;
        try
        {
            normalizedDomain = NormalizeDomain(domain, parameterName);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("SMTP Message-ID is invalid.", parameterName, exception);
        }

        if (separator <= 0
            || separator != content.LastIndexOf('@')
            || separator == content.Length - 1
            || !Guid.TryParseExact(content[..separator], "D", out Guid parsed)
            || !content[..separator].Equals(
                parsed.ToString("D", CultureInfo.InvariantCulture).AsSpan(),
                StringComparison.Ordinal)
            || !string.Equals(domain, normalizedDomain, StringComparison.Ordinal))
        {
            throw new ArgumentException("SMTP Message-ID is invalid.", parameterName);
        }

        return value;
    }

    internal static string EncodeDisplayName(string value)
    {
        ValidateDisplayName(value, nameof(value));
        if (value.All(static character => char.IsAsciiLetterOrDigit(character)
                || character is ' ' or '-' or '_' or '.'))
        {
            return value;
        }

        return string.Concat(
            "=?UTF-8?B?",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)),
            "?=");
    }

    private static void ValidateHeader(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsControl(character)))
        {
            throw new ArgumentException("Email header value is invalid.", parameterName);
        }
    }

    private static bool IsCanonicalDnsDomain(string domain)
    {
        if (domain.Length is < 1 or > 253
            || domain[0] == '.'
            || domain[^1] == '.')
        {
            return false;
        }

        foreach (string label in domain.Split('.'))
        {
            if (label.Length is < 1 or > 63
                || !char.IsAsciiLetterOrDigit(label[0])
                || !char.IsAsciiLetterOrDigit(label[^1])
                || label.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMailboxLocalPartCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character)
            || character is '.' or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
                or '+' or '-' or '/' or '=' or '?' or '^' or '_' or '`' or '{' or '|'
                or '}' or '~';
}
