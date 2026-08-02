using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Npgsql;
using PoolAI.Infrastructure.Postgres;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.GroupQuota.Infrastructure.Persistence;

internal sealed class PostgresGroupQuotaEventFactReader : IGroupQuotaEventFactReader
{
    private const string ReadSql = """
        SELECT
            event.id,
            event.event_sequence,
            event.group_id,
            event.period_id,
            event.reservation_id,
            event.attempt_id,
            event.event_type,
            event.delta_total_tokens,
            event.delta_consumed_tokens,
            event.delta_reserved_tokens,
            event.total_tokens_after,
            event.consumed_tokens_after,
            event.reserved_tokens_after,
            event.occurred_at,
            pg_catalog.jsonb_strip_nulls(event.metadata)::text
        FROM public.group_quota_events AS event
        WHERE event.group_id = $1
          AND event.event_sequence = $2;
        """;

    public async ValueTask<GroupQuotaEventFactSnapshot?> ReadAsync(
        EntityId groupId,
        long sourceEventSequence,
        IUnitOfWorkContext unitOfWorkContext,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceEventSequence);
        PostgresTransactionSession session = PostgresUnitOfWorkAccessor.Require(
            unitOfWorkContext);
        using NpgsqlCommand command = session.CreateCommand(ReadSql);
        command.Parameters.AddWithValue(groupId.Value);
        command.Parameters.AddWithValue(sourceEventSequence);
        using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        GroupQuotaEventFactSnapshot snapshot;
        try
        {
            snapshot = Read(reader);
        }
        catch (Exception exception) when (exception is
            ArgumentException or FormatException or JsonException or OverflowException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota-event fact violated its ABI.",
                exception);
        }
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota-event fact query returned duplicate identities.");
        }

        return snapshot;
    }

    private static GroupQuotaEventFactSnapshot Read(NpgsqlDataReader reader)
    {
        EntityId eventId = new(reader.GetGuid(0));
        long sourceEventSequence = reader.GetInt64(1);
        EntityId groupId = new(reader.GetGuid(2));
        EntityId periodId = new(reader.GetGuid(3));
        EntityId? reservationId = ReadOptionalId(reader, 4);
        EntityId? attemptId = ReadOptionalId(reader, 5);
        string eventType = reader.GetString(6);
        BigInteger deltaTotal = reader.GetFieldValue<BigInteger>(7);
        BigInteger deltaConsumed = reader.GetFieldValue<BigInteger>(8);
        BigInteger deltaReserved = reader.GetFieldValue<BigInteger>(9);
        BigInteger total = reader.GetFieldValue<BigInteger>(10);
        BigInteger consumed = reader.GetFieldValue<BigInteger>(11);
        BigInteger reserved = reader.GetFieldValue<BigInteger>(12);
        DateTimeOffset occurredAt = reader.GetFieldValue<DateTimeOffset>(13);
        JsonElement metadata = ParseMetadata(reader.GetString(14));
        EntityId correlationId = ReadCorrelationId(metadata, eventId);
        Validate(
            sourceEventSequence,
            reservationId,
            attemptId,
            eventType,
            total,
            consumed,
            reserved);
        return new GroupQuotaEventFactSnapshot(
            eventId,
            sourceEventSequence,
            correlationId,
            attemptId,
            groupId,
            periodId,
            reservationId,
            attemptId,
            eventType,
            deltaTotal,
            deltaConsumed,
            deltaReserved,
            total,
            consumed,
            reserved,
            occurredAt,
            metadata);
    }

    private static EntityId? ReadOptionalId(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new EntityId(reader.GetGuid(ordinal));

    private static JsonElement ParseMetadata(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota-event metadata violated its object ABI.");
        }

        return document.RootElement.Clone();
    }

    private static EntityId ReadCorrelationId(JsonElement metadata, EntityId eventId)
    {
        if (!metadata.TryGetProperty("request_id", out JsonElement requestId))
        {
            return eventId;
        }

        if (requestId.ValueKind == JsonValueKind.Null
            || requestId.ValueKind == JsonValueKind.String
            && string.IsNullOrEmpty(requestId.GetString()))
        {
            return eventId;
        }

        if (requestId.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(
                requestId.GetString(),
                "D",
                out Guid parsed)
            || parsed == Guid.Empty)
        {
            throw new InvalidOperationException(
                "The PostgreSQL quota-event request correlation violated its ABI.");
        }

        return new EntityId(parsed);
    }

    private static void Validate(
        long sourceEventSequence,
        EntityId? reservationId,
        EntityId? attemptId,
        string eventType,
        BigInteger total,
        BigInteger consumed,
        BigInteger reserved)
    {
        if (sourceEventSequence <= 0
            || !IsKnownEventType(eventType)
            || (reservationId is null) != (attemptId is null)
            || total < BigInteger.One
            || total > new BigInteger(9_007_199_254_740_991L)
            || consumed < BigInteger.Zero
            || reserved < BigInteger.Zero)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The PostgreSQL quota-event fact violated its ABI at source sequence {sourceEventSequence}."));
        }
    }

    private static bool IsKnownEventType(string eventType) => eventType is
        "initialized"
        or "reserved"
        or "dispatch_started"
        or "renewed"
        or "settled"
        or "released"
        or "expired"
        or "usage_adjusted"
        or "total_adjusted"
        or "period_reset";
}
