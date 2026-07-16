using System.Globalization;
using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Application;

internal sealed class IdentityPolicy
{
    internal IdentityPolicy(
        Uri publicBaseUrl,
        int passwordMinimumLength,
        TimeSpan passwordResetLifetime,
        string messageIdDomain,
        byte[] requestHashPepper)
    {
        PublicBaseUrl = publicBaseUrl ?? throw new ArgumentNullException(nameof(publicBaseUrl));
        PasswordMinimumLength = passwordMinimumLength;
        PasswordResetLifetime = passwordResetLifetime;
        MessageIdDomain = messageIdDomain ?? throw new ArgumentNullException(nameof(messageIdDomain));
        RequestHashPepper = requestHashPepper ?? throw new ArgumentNullException(nameof(requestHashPepper));
    }

    internal Uri PublicBaseUrl { get; }

    internal int PasswordMinimumLength { get; }

    internal TimeSpan PasswordResetLifetime { get; }

    internal string MessageIdDomain { get; }

    internal byte[] RequestHashPepper { get; }

    internal string BuildPasswordResetUrl(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return PublicBaseUrl.AbsoluteUri.TrimEnd('/')
            + "/reset-password?token="
            + token;
    }

    internal string BuildMessageId(EntityId emailOutboxId) => string.Create(
        CultureInfo.InvariantCulture,
        $"<{emailOutboxId.Value:D}@{MessageIdDomain}>");
}
