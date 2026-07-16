using System.Globalization;
using System.Net.Mail;

namespace PoolAI.Modules.Identity.Domain;

internal static class IdentityInput
{
    internal static string NormalizeEmail(string email)
    {
        string normalized = NormalizeMailbox(email, nameof(email));
        return normalized.ToLowerInvariant();
    }

    private static string NormalizeMailbox(string value, string parameterName)
    {
        MailAddress address = ParseMailbox(value, parameterName);
        int separator = address.Address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Address.Length - 1)
        {
            throw new ArgumentException("The email address is invalid.", parameterName);
        }

        string localPart = address.Address[..separator];
        ValidateLocalPart(localPart, parameterName);
        string asciiDomain = NormalizeDomain(
            address.Address[(separator + 1)..],
            parameterName);

        string canonical = string.Concat(localPart, "@", asciiDomain);
        if (canonical.Length > 254)
        {
            throw new ArgumentException("The email address is invalid.", parameterName);
        }

        return canonical;
    }

    private static MailAddress ParseMailbox(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 320
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsControl(character)))
        {
            throw new ArgumentException("The email address is invalid.", parameterName);
        }

        try
        {
            MailAddress address = new(value);
            return string.Equals(address.Address, value, StringComparison.Ordinal)
                ? address
                : throw new ArgumentException("The email address is invalid.", parameterName);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The email address is invalid.", parameterName, exception);
        }
    }

    private static void ValidateLocalPart(string localPart, string parameterName)
    {
        if (localPart.Length is < 1 or > 64
            || localPart[0] == '.'
            || localPart[^1] == '.'
            || localPart.Contains("..", StringComparison.Ordinal)
            || !localPart.All(IsMailboxLocalPartCharacter))
        {
            throw new ArgumentException("The email address is invalid.", parameterName);
        }
    }

    private static string NormalizeDomain(string domain, string parameterName)
    {
        try
        {
            string asciiDomain = new IdnMapping
            {
                UseStd3AsciiRules = true,
            }.GetAscii(domain).ToLowerInvariant();
            return IsCanonicalDnsDomain(asciiDomain)
                ? asciiDomain
                : throw new ArgumentException("The email address is invalid.", parameterName);
        }
        catch (ArgumentException exception) when (!string.Equals(
            exception.ParamName,
            parameterName,
            StringComparison.Ordinal))
        {
            throw new ArgumentException("The email address is invalid.", parameterName, exception);
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

    internal static string DisplayName(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        string trimmed = displayName.Trim();
        if (trimmed.Length > 100 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("The display name is invalid.", nameof(displayName));
        }

        return trimmed;
    }

    internal static string Reason(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string trimmed = reason.Trim();
        if (trimmed.Length > 500 || trimmed.Any(static character => character is '\r' or '\n'))
        {
            throw new ArgumentException("The reason is invalid.", nameof(reason));
        }

        return trimmed;
    }

    internal static void Password(string password, int minimumLength)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length < minimumLength || password.Length > 1024)
        {
            throw new ArgumentException("The password does not satisfy the configured length policy.", nameof(password));
        }
    }

    internal static void IdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 128 || key.Any(static character => character is < '!' or > '~'))
        {
            throw new ArgumentException("The idempotency key must contain 1..128 visible ASCII characters.", nameof(key));
        }
    }
}
