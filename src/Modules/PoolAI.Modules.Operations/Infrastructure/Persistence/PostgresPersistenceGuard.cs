using System.Net;
using Npgsql;
using NpgsqlTypes;

namespace PoolAI.Modules.Operations.Infrastructure.Persistence;

internal static class PostgresPersistenceGuard
{
    internal static void NotBlank(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("The value cannot have leading or trailing whitespace.", parameterName);
        }
    }

    internal static void Hash32(ReadOnlyMemory<byte> hash, string parameterName)
    {
        if (hash.Length != 32)
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 32 bytes.", parameterName);
        }
    }

    internal static void Positive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The duration must be positive.");
        }
    }

    internal static void JsonObject(JsonElement value, string parameterName)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The JSON value must be an object.", parameterName);
        }
    }

    internal static void NullableJsonObject(JsonElement? value, string parameterName)
    {
        if (value is { ValueKind: not JsonValueKind.Object })
        {
            throw new ArgumentException("The JSON value must be an object when supplied.", parameterName);
        }
    }

    internal static IPAddress? IpAddressOrNull(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new ArgumentException("The value must be an IPv4 or IPv6 address.", parameterName);
        }

        return address;
    }

    internal static void AddNullable(
        NpgsqlParameterCollection parameters,
        NpgsqlDbType type,
        object? value) =>
        parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = type,
            Value = value ?? DBNull.Value,
        });
}
