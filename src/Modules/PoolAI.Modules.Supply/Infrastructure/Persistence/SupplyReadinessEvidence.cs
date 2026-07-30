using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PoolAI.Modules.Supply.Infrastructure.Persistence;

internal static class SupplyReadinessEvidence
{
    private const string VersionPrefix = "v1.";

    internal static string Create(
        string canonicalSnapshotJson,
        DateTimeOffset observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalSnapshotJson);
        using JsonDocument document = JsonDocument.Parse(canonicalSnapshotJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The Supply readiness snapshot must be a JSON object.");
        }

        ValidateRedaction(document.RootElement);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("observed_at", observedAt.ToUniversalTime());
            writer.WritePropertyName("snapshot");
            WriteCanonical(writer, document.RootElement);
            writer.WriteEndObject();
        }

        byte[] digest = SHA256.HashData(stream.GetBuffer().AsSpan(
            0,
            checked((int)stream.Length)));
        try
        {
            return VersionPrefix + Convert
                .ToBase64String(digest)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void ValidateRedaction(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string normalized = NormalizePropertyName(property.Name);
                    if (normalized.Contains("credential", StringComparison.Ordinal)
                        || normalized.Contains("secret", StringComparison.Ordinal)
                        || normalized.Contains("baseurl", StringComparison.Ordinal)
                        || normalized.Contains("apikey", StringComparison.Ordinal)
                        || normalized.Contains("authorization", StringComparison.Ordinal)
                        || normalized.Contains("password", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "The Supply readiness snapshot contains a forbidden field.");
                    }

                    ValidateRedaction(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateRedaction(item);
                }

                break;
        }
    }

    private static string NormalizePropertyName(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value
                    .EnumerateObject()
                    .OrderBy(
                        static property => property.Name,
                        StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    "The Supply readiness snapshot contains an unsupported JSON value.");
        }
    }
}
