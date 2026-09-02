using System.Buffers;
using System.Text;

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
        if (!HasAtMostUnicodeScalars(normalized, 500))
        {
            throw new ArgumentException(
                "A reason must contain between 1 and 500 valid Unicode scalar values.",
                nameof(value));
        }

        return normalized;
    }

    internal static int RequestsPerMinute(int value)
    {
        if (value is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "A Group requests-per-minute limit must be between 1 and 1000000.");
        }

        return value;
    }

    private static bool HasAtMostUnicodeScalars(string value, int maximum)
    {
        if (value.Length == 0)
        {
            return false;
        }

        int count = 0;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(
                    remaining,
                    out _,
                    out int consumed) != OperationStatus.Done
                || ++count > maximum)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return true;
    }
}
