using System.Buffers;
using System.Collections.Frozen;

namespace PoolAI.Modules.Operations;

internal static class OutboxTelemetryClassifier
{
    internal static FrozenSet<string> Topics { get; } = new[]
    {
        "poolai.group.v1",
        "poolai.identity.v1",
        "poolai.quota.v1",
        "poolai.subscription-access.v1",
        "poolai.supply.v1",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> EventTypes { get; } = new[]
    {
        "account_created",
        "account_retired",
        "account_updated",
        "channel_created",
        "channel_retired",
        "channel_updated",
        "dispatch_started",
        "expired",
        "group_activated",
        "group_created",
        "group_supply_configuration_created",
        "group_supply_configuration_updated",
        "group_updated",
        "initialized",
        "password_reset_completed",
        "password_reset_requested",
        "period_reset",
        "released",
        "renewed",
        "reserved",
        "settled",
        "subscription_assigned",
        "subscription_updated",
        "template_created",
        "template_retired",
        "template_updated",
        "total_adjusted",
        "usage_adjusted",
        "user_created",
        "user_updated",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static FrozenSet<string> Reasons { get; } = new[]
    {
        "checkpoint_busy",
        "checkpoint_cas_lost",
        "consumer_exception",
        "created",
        "dependency_unavailable",
        "inbox_message_conflict",
        "inbox_sequence_conflict",
        "invalid_consumer_result",
        "invalid_inbox_result",
        "invalid_quota_event",
        "lineage_checkpoint_mismatch",
        "maximum_attempts",
        "quota_fact_contract_invalid",
        "quota_fact_mismatch",
        "quota_fact_reference_missing",
        "quota_event_fact_contract_invalid",
        "quota_event_fact_mismatch",
        "quota_partition_mismatch",
        "source_sequence_stale",
        "unknown",
        "unregistered_topic",
        "unsupported_schema_version",
        "usage_projection_invalid",
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static string NormalizeTopic(string value) =>
        Topics.Contains(value) ? value : "other";

    internal static string NormalizeEventType(string value) =>
        EventTypes.Contains(value) ? value : "other";

    internal static string NormalizeReason(string value) =>
        Reasons.Contains(value) ? value : "unknown";

    internal static JsonElement NormalizeOperationalPayload(JsonElement payload)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            WriteNormalized(writer, payload, propertyName: null);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteNormalized(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName)
    {
        if (TryWriteClassifiedValue(writer, value, propertyName))
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray())
                {
                    WriteNormalized(writer, item, propertyName);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool TryWriteClassifiedValue(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName)
    {
        Func<string, string>? normalize = propertyName switch
        {
            "topic" => NormalizeTopic,
            "event_type" or "eventType" => NormalizeEventType,
            "reason" => NormalizeReason,
            _ => null,
        };
        if (normalize is null)
        {
            return false;
        }

        string raw = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
        writer.WriteStringValue(normalize(raw));
        return true;
    }
}
