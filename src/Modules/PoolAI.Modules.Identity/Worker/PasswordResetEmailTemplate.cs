using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Worker;

internal static class PasswordResetEmailTemplate
{
    internal const string Code = "password-reset-v1";
    internal const string Subject = "Reset your PoolAI password";

    internal static EmailTransportMessage Render(
        EmailOutboxMessage message,
        EmailSecretEnvelopePlaintext plaintext,
        EmailOutboxWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(message.TemplateCode, Code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Email template code is unsupported.");
        }

        _ = EmailHeaderValueValidator.ValidateMessageId(
            message.MessageId,
            nameof(message));
        string expectedMessageIdPrefix = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"<{message.Lease.EmailId.Value:D}@");
        if (!message.MessageId.StartsWith(expectedMessageIdPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Email Message-ID does not match its outbox row.", nameof(message));
        }

        int expiresInMinutes = ReadExpiry(message.TemplatePayload);
        if (!Uri.TryCreate(plaintext.ResetUrl, UriKind.Absolute, out Uri? resetUri)
            || (!string.Equals(resetUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    resetUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(resetUri.Fragment)
            || plaintext.ResetUrl.Any(static character => character is '\r' or '\n' or '\0'))
        {
            throw new InvalidOperationException("Password-reset URL is invalid.");
        }

        string body = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Use the following URL to reset your PoolAI password:\n\n{plaintext.ResetUrl}\n\nThis link expires in {expiresInMinutes} minutes.\nIf you did not request this reset, you can ignore this email.\n");
        return new EmailTransportMessage(
            options.FromAddress,
            options.FromName,
            plaintext.Recipient,
            Subject,
            message.MessageId,
            body);
    }

    private static int ReadExpiry(JsonElement payload)
    {
        if (payload.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidOperationException("Password-reset template payload is invalid.");
        }

        int propertyCount = 0;
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            propertyCount++;
            if (!string.Equals(property.Name, "expires_in_minutes", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Password-reset template payload is invalid.");
            }
        }

        if (propertyCount != 1
            || !payload.TryGetProperty("expires_in_minutes", out JsonElement expiry)
            || !expiry.TryGetInt32(out int expiresInMinutes)
            || expiresInMinutes is < 5 or > 60)
        {
            throw new InvalidOperationException("Password-reset template payload is invalid.");
        }

        return expiresInMinutes;
    }
}
