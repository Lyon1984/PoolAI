using System.Diagnostics.CodeAnalysis;

namespace PoolAI.Modules.Identity.Abstractions;

public static class ApiKeyTextRules
{
    public static bool IsValidName(
        [NotNullWhen(true)] string? value) =>
        IsValid(value, maximumScalarCount: 100);

    public static bool IsValidAdminReason(
        [NotNullWhen(true)] string? value) =>
        IsValid(value, maximumScalarCount: 500);

    private static bool IsValid(
        [NotNullWhen(true)] string? value,
        int maximumScalarCount)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int scalarCount = 0;
        bool hasNonWhitespace = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            int scalar;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length
                    || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                scalar = char.ConvertToUtf32(character, value[++index]);
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
            else
            {
                scalar = character;
            }

            scalarCount++;
            if (scalarCount > maximumScalarCount
                || IsForbiddenControl(scalar))
            {
                return false;
            }

            hasNonWhitespace |= !IsFrozenWhiteSpace(scalar);
        }

        return scalarCount > 0 && hasNonWhitespace;
    }

    private static bool IsForbiddenControl(int scalar) =>
        scalar is >= 0x00 and <= 0x1f
            or >= 0x7f and <= 0x9f
            or 0x2028
            or 0x2029;

    private static bool IsFrozenWhiteSpace(int scalar) => scalar is
        0x0009 or 0x000a or 0x000b or 0x000c or 0x000d or 0x0020 or
        0x0085 or 0x00a0 or 0x1680 or
        >= 0x2000 and <= 0x200a or
        0x2028 or 0x2029 or 0x202f or 0x205f or 0x3000;
}
