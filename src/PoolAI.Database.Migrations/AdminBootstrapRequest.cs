using System.Globalization;
using System.Net.Mail;

namespace PoolAI.Database.Migrations;

public sealed class AdminBootstrapRequest
{
    public AdminBootstrapRequest(
        string email,
        string displayName,
        AdminBootstrapSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        string candidateEmail = NormalizeMailbox(email, nameof(email));

        string candidateDisplayName = displayName?.Trim() ?? string.Empty;
        if (candidateDisplayName.Length is < 1 or > 100
            || candidateDisplayName.Contains('\0')
            || candidateDisplayName.Contains('\r')
            || candidateDisplayName.Contains('\n'))
        {
            throw new ArgumentException(
                "Bootstrap display-name input is invalid.",
                nameof(displayName));
        }

        Email = candidateEmail;
        NormalizedEmail = candidateEmail.ToLowerInvariant();
        DisplayName = candidateDisplayName;
        Secrets = secrets;
    }

    public string Email { get; }

    public string NormalizedEmail { get; }

    public string DisplayName { get; }

    public AdminBootstrapSecrets Secrets { get; }

    public override string ToString() => "[Admin bootstrap request]";

    private static string NormalizeMailbox(string? value, string parameterName)
    {
        MailAddress address = ParseMailbox(value, parameterName);
        int separator = address.Address.LastIndexOf('@');
        if (separator <= 0 || separator == address.Address.Length - 1)
        {
            throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
        }

        string localPart = address.Address[..separator];
        ValidateLocalPart(localPart, parameterName);
        string asciiDomain = NormalizeDomain(
            address.Address[(separator + 1)..],
            parameterName);

        string canonical = string.Concat(localPart, "@", asciiDomain);
        if (canonical.Length > 254)
        {
            throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
        }

        return canonical;
    }

    private static MailAddress ParseMailbox(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 320
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsControl(character)))
        {
            throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
        }

        try
        {
            MailAddress address = new(value);
            return string.Equals(address.Address, value, StringComparison.Ordinal)
                ? address
                : throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException(
                "Bootstrap email input is invalid.",
                parameterName,
                exception);
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
            throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
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
                : throw new ArgumentException("Bootstrap email input is invalid.", parameterName);
        }
        catch (ArgumentException exception) when (!string.Equals(
            exception.ParamName,
            parameterName,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Bootstrap email input is invalid.",
                parameterName,
                exception);
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
