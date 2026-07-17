namespace PoolAI.Modules.GroupQuota.Domain;

internal static class GroupInput
{
    internal static string Name(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 100
            || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A Group name must contain between 1 and 100 non-control characters.",
                nameof(value));
        }

        return normalized;
    }

    internal static string? Description(string? value)
    {
        if (value is { Length: > 1000 })
        {
            throw new ArgumentException(
                "A Group description cannot exceed 1000 characters.",
                nameof(value));
        }

        return value;
    }

    internal static string IdempotencyKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < 1 or > 128
            || value.Any(static character => character is < '\x21' or > '\x7e'))
        {
            throw new ArgumentException(
                "An idempotency key must contain 1 to 128 visible ASCII characters.",
                nameof(value));
        }

        return value;
    }

    internal static string Reason(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = value.Trim();
        if (normalized.Length is < 1 or > 500
            || normalized.Any(static character => character is '\r' or '\n'))
        {
            throw new ArgumentException(
                "A reason must contain between 1 and 500 characters.",
                nameof(value));
        }

        return normalized;
    }
}
