using System.Buffers;
using System.Globalization;
using System.Text;

namespace PoolAI.Modules.Operations.Application;

internal static class OutboxReplayInput
{
    internal static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: >= 1 and <= 128 }
        && value.All(static character => character is >= '\x21' and <= '\x7e');

    internal static bool TryNormalizeReason(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int scalarCount = 0;
        bool hasNonWhitespace = false;
        ReadOnlySpan<char> remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(
                    remaining,
                    out Rune rune,
                    out int consumed) != OperationStatus.Done)
            {
                return false;
            }

            scalarCount++;
            if (scalarCount > 500
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control)
            {
                return false;
            }

            hasNonWhitespace |= !Rune.IsWhiteSpace(rune);
            remaining = remaining[consumed..];
        }

        if (!hasNonWhitespace)
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length > 0;
    }
}
