using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Worker;

namespace PoolAI.Modules.Identity.Infrastructure;

internal static class IdentityOptions
{
    internal static IdentityPolicy FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Uri.TryCreate(
                configuration["App:PublicBaseUrl"],
                UriKind.Absolute,
                out Uri? publicBaseUrl)
            || (!string.Equals(publicBaseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                && !string.Equals(publicBaseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            || !string.IsNullOrEmpty(publicBaseUrl.Query)
            || !string.IsNullOrEmpty(publicBaseUrl.Fragment))
        {
            throw new InvalidOperationException("App:PublicBaseUrl is invalid.");
        }

        int minimumLength = configuration.GetValue("Auth:Password:MinLength", 12);
        int resetMinutes = configuration.GetValue("Auth:PasswordReset:TokenMinutes", 30);
        if (minimumLength is < 12 or > 128 || resetMinutes is < 5 or > 60)
        {
            throw new InvalidOperationException("Identity password configuration is invalid.");
        }

        string fromAddress = NormalizeFromAddress(configuration);
        int separator = fromAddress.LastIndexOf('@');
        string domain = fromAddress[(separator + 1)..];
        byte[] requestHashPepper;
        try
        {
            requestHashPepper = Convert.FromBase64String(
                configuration["Idempotency:RequestHashPepper"] ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper is invalid.",
                exception);
        }

        if (requestHashPepper.Length < 32)
        {
            throw new InvalidOperationException(
                "Idempotency:RequestHashPepper must contain at least 256 bits.");
        }

        return new IdentityPolicy(
            publicBaseUrl,
            minimumLength,
            TimeSpan.FromMinutes(resetMinutes),
            domain,
            requestHashPepper);
    }

    private static string NormalizeFromAddress(IConfiguration configuration)
    {
        try
        {
            return EmailHeaderValueValidator.NormalizeMailbox(
                configuration["Email:FromAddress"]
                    ?? throw new InvalidOperationException("Email:FromAddress is required."),
                "Email:FromAddress");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Email:FromAddress is invalid.", exception);
        }
    }
}
