namespace PoolAI.Modules.SubscriptionAccess.Domain;

internal static class SubscriptionInput
{
    internal static string IdempotencyKey(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > 128
            || value.Any(static character => character is < (char)0x21 or > (char)0x7e))
        {
            throw new ArgumentException("The idempotency key is invalid.", nameof(value));
        }

        return value;
    }

    internal static string Name(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 100 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The name is invalid.", nameof(value));
        }

        return normalized;
    }

    internal static string? Description(string? value)
    {
        if (value is not null && (value.Length > 1000 || value.Any(char.IsControl)))
        {
            throw new ArgumentException("The description is invalid.", nameof(value));
        }

        return value;
    }

    internal static int DurationDays(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 3650);
        return value;
    }

    internal static string Reason(string value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 500 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("The change reason is invalid.", nameof(value));
        }

        return normalized;
    }

    internal static void ExpectedVersion(long value) =>
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

    internal static void TimeRange(DateTimeOffset startsAt, DateTimeOffset expiresAt)
    {
        if (expiresAt <= startsAt)
        {
            throw new ArgumentException(
                "Subscription expiry must be after its start.",
                nameof(expiresAt));
        }
    }
}
