using PoolAI.Modules.Identity.Abstractions;

namespace PoolAI.Modules.Identity.Domain;

internal static class ApiKeyResourceValidator
{
    internal static void EnsureValid(ApiKeyResource value)
    {
        if (!IsValid(value))
        {
            throw new InvalidOperationException(
                "The API Key repository returned an invalid resource.");
        }
    }

    internal static bool IsValid(ApiKeyResource? value)
    {
        if (value is null
            || value.Id.Value == Guid.Empty
            || value.UserId.Value == Guid.Empty
            || value.GroupId.Value == Guid.Empty
            || !IsCanonicalName(value.Name)
            || !IsDisplayPrefix(value.Prefix)
            || !Enum.IsDefined(value.Status)
            || !Enum.IsDefined(value.EffectiveStatus)
            || value.Version <= 0
            || value.CreatedAt == default
            || value.UpdatedAt == default
            || value.ObservedAt == default
            || value.CreatedAt > value.UpdatedAt
            || value.UpdatedAt > value.ObservedAt
            || value.ExpiresAt is DateTimeOffset expiresAt
                && expiresAt.Offset != TimeSpan.Zero
            || value.LastUsedAt is DateTimeOffset lastUsedAt
                && (lastUsedAt.Offset != TimeSpan.Zero
                    || lastUsedAt > value.ObservedAt)
            || value.CreatedAt.Offset != TimeSpan.Zero
            || value.UpdatedAt.Offset != TimeSpan.Zero
            || value.ObservedAt.Offset != TimeSpan.Zero
            || value.ExpiresAt is DateTimeOffset expiry
                && expiry <= value.CreatedAt
            || value.LastUsedAt is DateTimeOffset usedAt
                && usedAt < value.CreatedAt
            || EffectiveStatus(value) != value.EffectiveStatus)
        {
            return false;
        }

        try
        {
            IReadOnlyList<string> canonical =
                ApiKeyInput.AllowedCidrs(value.AllowedCidrs);
            return canonical.SequenceEqual(
                value.AllowedCidrs,
                StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsDisplayPrefix(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length is >= 13 and <= 24
        && value.StartsWith("sk-", StringComparison.Ordinal)
        && value.AsSpan(3).Length is >= 10 and <= 21
        && HasOnlyCredentialCharacters(value.AsSpan(3));

    private static bool IsCanonicalName(string value)
    {
        try
        {
            return string.Equals(
                ApiKeyInput.Name(value),
                value,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ApiKeyEffectiveStatus EffectiveStatus(ApiKeyResource value)
    {
        if (value.Status == ApiKeyPersistentStatus.Revoked)
        {
            return ApiKeyEffectiveStatus.Revoked;
        }

        if (value.Status == ApiKeyPersistentStatus.Disabled)
        {
            return ApiKeyEffectiveStatus.Disabled;
        }

        return value.ExpiresAt is DateTimeOffset expiresAt
            && expiresAt <= value.ObservedAt
                ? ApiKeyEffectiveStatus.Expired
                : ApiKeyEffectiveStatus.Active;
    }

    private static bool HasOnlyCredentialCharacters(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return true;
    }
}
