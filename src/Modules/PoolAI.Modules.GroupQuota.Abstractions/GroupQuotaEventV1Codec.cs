using System.Collections.Frozen;
using System.Globalization;

namespace PoolAI.Modules.GroupQuota.Abstractions;

/// <summary>
/// Strict, BCL-only decoder for the poolai.quota.v1/schema v1 Published Language.
/// Known fields are validated fail-closed; unknown optional fields are accepted so v1 can
/// evolve additively without changing the meaning of existing fields.
/// </summary>
public static class GroupQuotaEventV1Codec
{
    public const string Topic = "poolai.quota.v1";
    public const int SchemaVersion = 1;
    public const string AggregateType = "group";

    private static readonly FrozenDictionary<
        string,
        Func<GroupQuotaEventV1Data, GroupQuotaEventV1>> EventFactories =
        new Dictionary<string, Func<GroupQuotaEventV1Data, GroupQuotaEventV1>>(
            StringComparer.Ordinal)
        {
            ["initialized"] = static data => new GroupQuotaInitializedEventV1(data),
            ["reserved"] = static data => new GroupQuotaReservedEventV1(data),
            ["dispatch_started"] = static data => new GroupQuotaDispatchStartedEventV1(data),
            ["renewed"] = static data => new GroupQuotaRenewedEventV1(data),
            ["settled"] = static data => new GroupQuotaSettledEventV1(data),
            ["released"] = static data => new GroupQuotaReleasedEventV1(data),
            ["usage_adjusted"] = static data => new GroupQuotaUsageAdjustedEventV1(data),
            ["total_adjusted"] = static data => new GroupQuotaTotalAdjustedEventV1(data),
            ["period_reset"] = static data => new GroupQuotaPeriodResetEventV1(data),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> EventTypes = EventFactories.Keys
        .Append("expired")
        .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RequiredAttemptFactEventTypes = new[]
    {
        "settled",
        "usage_adjusted",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static GroupQuotaEventV1DecodeResult Decode(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return Decode(document.RootElement);
        }
        catch (JsonException)
        {
            return Failed(GroupQuotaEventV1DecodeFailureCode.MalformedJson, "$");
        }
    }

    public static GroupQuotaEventV1DecodeResult Decode(JsonElement envelope)
    {
        GroupQuotaEventV1DecodeFailure? shapeFailure = ValidateEnvelopeShape(envelope);
        if (shapeFailure is not null)
        {
            return Failed(shapeFailure);
        }

        ParseResult<EnvelopeDiscriminator> discriminatorResult =
            DecodeEnvelopeDiscriminator(envelope);
        if (discriminatorResult.Value is not { } discriminator)
        {
            return Failed(discriminatorResult.Failure!);
        }

        ParseResult<EnvelopePosition> positionResult = DecodeEnvelopePosition(envelope);
        if (positionResult.Value is not { } position)
        {
            return Failed(positionResult.Failure!);
        }

        ParseResult<EnvelopeContext> contextResult = DecodeEnvelopeContext(envelope);
        if (contextResult.Value is not { } context)
        {
            return Failed(contextResult.Failure!);
        }

        if (!envelope.TryGetProperty("payload", out JsonElement payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            return Failed(GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload");
        }

        ParseResult<GroupQuotaEventV1> payloadResult = DecodePayload(
            payload,
            discriminator,
            position,
            context);
        if (payloadResult.Value is not { } payloadEvent)
        {
            return Failed(payloadResult.Failure!);
        }

        return Successful(new GroupQuotaEventEnvelopeV1(
            position.MessageId,
            discriminator.Topic,
            discriminator.EventType,
            discriminator.SchemaVersion,
            position.EventSequence,
            position.SourceEventSequence,
            position.AggregateType,
            position.AggregateId,
            AggregateVersion: null,
            context.DeduplicationKey,
            context.OccurredAt,
            context.CorrelationId,
            context.CausationId,
            context.ReplayOf,
            payloadEvent));
    }

    private static GroupQuotaEventV1DecodeFailure? ValidateEnvelopeShape(JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return new GroupQuotaEventV1DecodeFailure(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$");
        }

        return TryFindDuplicateProperty(envelope, "$", out string? duplicateLocation)
            ? new GroupQuotaEventV1DecodeFailure(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                duplicateLocation!)
            : null;
    }

    private static ParseResult<EnvelopeDiscriminator> DecodeEnvelopeDiscriminator(
        JsonElement envelope)
    {
        if (!TryReadRequiredString(envelope, "topic", out string topic))
        {
            return ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.topic");
        }

        if (!string.Equals(topic, Topic, StringComparison.Ordinal))
        {
            return ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.UnsupportedTopic,
                "$.topic");
        }

        if (!TryReadInt32(envelope, "schema_version", out int schemaVersion))
        {
            return ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.schema_version");
        }

        if (schemaVersion != SchemaVersion)
        {
            return ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.UnsupportedSchemaVersion,
                "$.schema_version");
        }

        if (!TryReadRequiredString(envelope, "event_type", out string eventType))
        {
            return ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.event_type");
        }

        return EventTypes.Contains(eventType)
            ? Parsed(new EnvelopeDiscriminator(topic, schemaVersion, eventType))
            : ParseFailed<EnvelopeDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.UnsupportedEventType,
                "$.event_type");
    }

    private static ParseResult<EnvelopePosition> DecodeEnvelopePosition(JsonElement envelope)
    {
        if (!TryReadEntityId(envelope, "message_id", out EntityId messageId))
        {
            return ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.message_id");
        }

        if (!TryReadPositiveInt64(envelope, "event_sequence", out long eventSequence))
        {
            return ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.InvalidSequence,
                "$.event_sequence");
        }

        if (!TryReadPositiveInt64(envelope, "source_event_sequence", out long sourceSequence))
        {
            return ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.InvalidSequence,
                "$.source_event_sequence");
        }

        if (!TryReadRequiredString(envelope, "aggregate_type", out string aggregateType)
            || !string.Equals(aggregateType, AggregateType, StringComparison.Ordinal))
        {
            return ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.aggregate_type");
        }

        if (!TryReadEntityId(envelope, "aggregate_id", out EntityId aggregateId))
        {
            return ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.aggregate_id");
        }

        return TryReadRequiredNull(envelope, "aggregate_version")
            ? Parsed(new EnvelopePosition(
                messageId,
                eventSequence,
                sourceSequence,
                aggregateType,
                aggregateId))
            : ParseFailed<EnvelopePosition>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.aggregate_version");
    }

    private static ParseResult<EnvelopeContext> DecodeEnvelopeContext(JsonElement envelope)
    {
        if (!TryReadRequiredString(envelope, "deduplication_key", out string deduplicationKey)
            || string.IsNullOrWhiteSpace(deduplicationKey))
        {
            return ParseFailed<EnvelopeContext>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.deduplication_key");
        }

        if (!TryReadDateTimeOffset(envelope, "occurred_at", out DateTimeOffset occurredAt))
        {
            return ParseFailed<EnvelopeContext>(
                GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
                "$.occurred_at");
        }

        if (!TryReadEntityId(envelope, "correlation_id", out EntityId correlationId))
        {
            return ParseFailed<EnvelopeContext>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.correlation_id");
        }

        if (!TryReadNullableEntityId(envelope, "causation_id", true, out EntityId? causationId))
        {
            return ParseFailed<EnvelopeContext>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.causation_id");
        }

        return TryReadNullableEntityId(envelope, "replay_of", true, out EntityId? replayOf)
            ? Parsed(new EnvelopeContext(
                deduplicationKey,
                occurredAt,
                correlationId,
                causationId,
                replayOf))
            : ParseFailed<EnvelopeContext>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.replay_of");
    }

    private static ParseResult<GroupQuotaEventV1> DecodePayload(
        JsonElement payload,
        EnvelopeDiscriminator envelopeDiscriminator,
        EnvelopePosition envelopePosition,
        EnvelopeContext envelopeContext)
    {
        ParseResult<PayloadDiscriminator> discriminatorResult =
            DecodePayloadDiscriminator(payload, envelopeDiscriminator);
        if (discriminatorResult.Value is not { } discriminator)
        {
            return ParseFailed<GroupQuotaEventV1>(discriminatorResult.Failure!);
        }

        ParseResult<PayloadIdentity> identityResult = DecodePayloadIdentity(
            payload,
            discriminator.EventType);
        if (identityResult.Value is not { } identity)
        {
            return ParseFailed<GroupQuotaEventV1>(identityResult.Failure!);
        }

        ParseResult<PayloadState> stateResult = DecodePayloadState(payload);
        if (stateResult.Value is not { } state)
        {
            return ParseFailed<GroupQuotaEventV1>(stateResult.Failure!);
        }

        GroupQuotaEventV1DecodeFailure? mismatch = FindEnvelopePayloadMismatch(
            discriminator,
            identity,
            state,
            envelopePosition,
            envelopeContext);
        if (mismatch is not null)
        {
            return ParseFailed<GroupQuotaEventV1>(mismatch);
        }

        GroupQuotaEventV1Data data = CreateData(discriminator, identity, state);
        return CreateEvent(discriminator.EventType, data);
    }

    private static ParseResult<PayloadDiscriminator> DecodePayloadDiscriminator(
        JsonElement payload,
        EnvelopeDiscriminator envelope)
    {
        if (!TryReadInt32(payload, "schema_version", out int schemaVersion))
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
                "$.payload.schema_version");
        }

        if (schemaVersion != envelope.SchemaVersion)
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch,
                "$.payload.schema_version");
        }

        if (!TryReadRequiredString(payload, "event_type", out string eventType))
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
                "$.payload.event_type");
        }

        if (!EventTypes.Contains(eventType))
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.UnsupportedEventType,
                "$.payload.event_type");
        }

        if (!string.Equals(eventType, envelope.EventType, StringComparison.Ordinal))
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch,
                "$.payload.event_type");
        }

        if (!TryReadEntityId(payload, "event_id", out EntityId eventId))
        {
            return ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
                "$.payload.event_id");
        }

        return TryReadPositiveInt64(payload, "source_event_sequence", out long sourceSequence)
            ? Parsed(new PayloadDiscriminator(eventId, eventType, sourceSequence))
            : ParseFailed<PayloadDiscriminator>(
                GroupQuotaEventV1DecodeFailureCode.InvalidSequence,
                "$.payload.source_event_sequence");
    }

    private static ParseResult<PayloadIdentity> DecodePayloadIdentity(
        JsonElement payload,
        string eventType)
    {
        if (!TryReadEntityId(payload, "correlation_id", out EntityId correlationId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("correlation_id");
        }

        if (!TryReadNullableEntityId(payload, "causation_id", false, out EntityId? causationId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("causation_id");
        }

        if (!TryReadEntityId(payload, "group_id", out EntityId groupId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("group_id");
        }

        if (!TryReadEntityId(payload, "period_id", out EntityId periodId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("period_id");
        }

        if (!TryReadNullableEntityId(payload, "reservation_id", false, out EntityId? reservationId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("reservation_id");
        }

        if (!TryReadNullableEntityId(payload, "attempt_id", false, out EntityId? attemptId))
        {
            return MissingPayloadIdentity<PayloadIdentity>("attempt_id");
        }

        if (RequiredAttemptFactEventTypes.Contains(eventType)
            && (reservationId is null || attemptId is null))
        {
            return MissingPayloadIdentity<PayloadIdentity>(
                reservationId is null ? "reservation_id" : "attempt_id");
        }

        return Parsed(new PayloadIdentity(
            correlationId,
            causationId,
            groupId,
            periodId,
            reservationId,
            attemptId));
    }

    private static ParseResult<PayloadState> DecodePayloadState(JsonElement payload)
    {
        BigInteger deltaTotal = default;
        BigInteger deltaConsumed = default;
        BigInteger deltaReserved = default;
        BigInteger total = default;
        BigInteger consumed = default;
        BigInteger reserved = default;
        bool valid =
            TryReadTokenCount(payload, "delta_total_tokens", true, out deltaTotal)
            && TryReadTokenCount(payload, "delta_consumed_tokens", true, out deltaConsumed)
            && TryReadTokenCount(payload, "delta_reserved_tokens", true, out deltaReserved)
            && TryReadTokenCount(payload, "total_tokens", false, out total)
            && TryReadTokenCount(payload, "consumed_tokens", false, out consumed)
            && TryReadTokenCount(payload, "reserved_tokens", false, out reserved);
        if (!valid)
        {
            return ParseFailed<PayloadState>(
                GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
                "$.payload");
        }

        if (!TryReadDateTimeOffset(payload, "occurred_at", out DateTimeOffset occurredAt))
        {
            return ParseFailed<PayloadState>(
                GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
                "$.payload.occurred_at");
        }

        return payload.TryGetProperty("metadata", out JsonElement metadata)
            && metadata.ValueKind == JsonValueKind.Object
            ? Parsed(new PayloadState(
                deltaTotal,
                deltaConsumed,
                deltaReserved,
                total,
                consumed,
                reserved,
                occurredAt,
                metadata.Clone()))
            : ParseFailed<PayloadState>(
                GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
                "$.payload.metadata");
    }

    private static GroupQuotaEventV1DecodeFailure? FindEnvelopePayloadMismatch(
        PayloadDiscriminator discriminator,
        PayloadIdentity identity,
        PayloadState state,
        EnvelopePosition envelopePosition,
        EnvelopeContext envelopeContext)
    {
        if (discriminator.SourceEventSequence != envelopePosition.SourceEventSequence)
        {
            return Mismatch("source_event_sequence");
        }

        if (identity.GroupId != envelopePosition.AggregateId)
        {
            return Mismatch("group_id");
        }

        if (identity.CorrelationId != envelopeContext.CorrelationId)
        {
            return Mismatch("correlation_id");
        }

        if (identity.CausationId != envelopeContext.CausationId)
        {
            return Mismatch("causation_id");
        }

        return state.OccurredAt != envelopeContext.OccurredAt
            ? Mismatch("occurred_at")
            : null;
    }

    private static GroupQuotaEventV1Data CreateData(
        PayloadDiscriminator discriminator,
        PayloadIdentity identity,
        PayloadState state) =>
        new(
            discriminator.EventId,
            discriminator.SourceEventSequence,
            identity.CorrelationId,
            identity.CausationId,
            identity.GroupId,
            identity.PeriodId,
            identity.ReservationId,
            identity.AttemptId,
            state.DeltaTotalTokens,
            state.DeltaConsumedTokens,
            state.DeltaReservedTokens,
            state.TotalTokens,
            state.ConsumedTokens,
            state.ReservedTokens,
            state.OccurredAt,
            state.Metadata);

    private static ParseResult<GroupQuotaEventV1> CreateEvent(
        string eventType,
        GroupQuotaEventV1Data data)
    {
        if (string.Equals(eventType, "expired", StringComparison.Ordinal))
        {
            if (!data.Metadata.TryGetProperty("conservative_expiry", out JsonElement flag)
                || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return ParseFailed<GroupQuotaEventV1>(
                    GroupQuotaEventV1DecodeFailureCode.InvalidEventSemantics,
                    "$.payload.metadata.conservative_expiry");
            }

            bool conservativeExpiry = flag.GetBoolean();
            if (conservativeExpiry
                && (data.ReservationId is null || data.AttemptId is null))
            {
                return MissingPayloadIdentity<GroupQuotaEventV1>(
                    data.ReservationId is null ? "reservation_id" : "attempt_id");
            }

            return Parsed<GroupQuotaEventV1>(new GroupQuotaExpiredEventV1(
                data,
                conservativeExpiry));
        }

        return Parsed(EventFactories[eventType](data));
    }

    private static ParseResult<T> MissingPayloadIdentity<T>(string propertyName)
        where T : class =>
        ParseFailed<T>(
            GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
            $"$.payload.{propertyName}");

    private static GroupQuotaEventV1DecodeFailure Mismatch(string propertyName) =>
        new(
            GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch,
            $"$.payload.{propertyName}");

    private static GroupQuotaEventV1DecodeResult Successful(GroupQuotaEventEnvelopeV1 envelope) =>
        new(envelope, Failure: null);

    private static GroupQuotaEventV1DecodeResult Failed(
        GroupQuotaEventV1DecodeFailureCode code,
        string location) =>
        Failed(new GroupQuotaEventV1DecodeFailure(code, location));

    private static GroupQuotaEventV1DecodeResult Failed(
        GroupQuotaEventV1DecodeFailure failure) =>
        new(Envelope: null, failure);

    private static ParseResult<T> Parsed<T>(T value)
        where T : class =>
        new(value, Failure: null);

    private static ParseResult<T> ParseFailed<T>(
        GroupQuotaEventV1DecodeFailureCode code,
        string location)
        where T : class =>
        ParseFailed<T>(new GroupQuotaEventV1DecodeFailure(code, location));

    private static ParseResult<T> ParseFailed<T>(GroupQuotaEventV1DecodeFailure failure)
        where T : class =>
        new(Value: null, failure);

    private static bool TryReadRequiredString(
        JsonElement value,
        string propertyName,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = property.GetString()!;
        return true;
    }

    private static bool TryReadInt32(JsonElement value, string propertyName, out int result)
    {
        result = default;
        return value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out result);
    }

    private static bool TryReadPositiveInt64(
        JsonElement value,
        string propertyName,
        out long result)
    {
        result = default;
        return value.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out result)
            && result > 0;
    }

    private static bool TryReadRequiredNull(JsonElement value, string propertyName) =>
        value.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.Null;

    private static bool TryReadEntityId(
        JsonElement value,
        string propertyName,
        out EntityId result)
    {
        result = default;
        return value.TryGetProperty(propertyName, out JsonElement property)
            && TryParseEntityId(property, out result);
    }

    private static bool TryReadNullableEntityId(
        JsonElement value,
        string propertyName,
        bool requiredProperty,
        out EntityId? result)
    {
        result = null;
        if (!value.TryGetProperty(propertyName, out JsonElement property))
        {
            return !requiredProperty;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (!TryParseEntityId(property, out EntityId parsed))
        {
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryParseEntityId(JsonElement value, out EntityId result)
    {
        result = default;
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? text = value.GetString();
        if (text is null
            || !Guid.TryParseExact(text, "D", out Guid parsed)
            || parsed == Guid.Empty
            || !string.Equals(text, parsed.ToString("D"), StringComparison.Ordinal)
            || text[14] is < '1' or > '8'
            || text[19] is not ('8' or '9' or 'a' or 'b'))
        {
            return false;
        }

        result = new EntityId(parsed);
        return true;
    }

    private static bool TryReadDateTimeOffset(
        JsonElement value,
        string propertyName,
        out DateTimeOffset result)
    {
        result = default;
        if (!TryReadRequiredString(value, propertyName, out string text)
            || !text.Contains('T', StringComparison.Ordinal)
            || !(text.EndsWith('Z') || HasNumericOffset(text)))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result);
    }

    private static bool HasNumericOffset(string text)
    {
        int timeSeparator = text.IndexOf('T', StringComparison.Ordinal);
        int plus = text.LastIndexOf('+');
        int minus = text.LastIndexOf('-');
        return plus > timeSeparator || minus > timeSeparator;
    }

    private static bool TryReadTokenCount(
        JsonElement value,
        string propertyName,
        bool allowNegative,
        out BigInteger result)
    {
        result = default;
        if (!TryReadRequiredString(value, propertyName, out string text) || text.Length == 0)
        {
            return false;
        }

        int digitStart = text[0] == '-' ? 1 : 0;
        int digitCount = text.Length - digitStart;
        if ((digitStart == 1 && !allowNegative)
            || digitCount is < 1 or > 78
            || (digitCount > 1 && text[digitStart] == '0')
            || (digitStart == 1 && digitCount == 1 && text[1] == '0'))
        {
            return false;
        }

        for (int index = digitStart; index < text.Length; index++)
        {
            if (text[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return BigInteger.TryParse(
            text,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out result);
    }

    private static bool TryFindDuplicateProperty(
        JsonElement value,
        string location,
        out string? duplicateLocation)
    {
        duplicateLocation = null;
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string childLocation = $"{location}.{property.Name}";
                if (!names.Add(property.Name)
                    || TryFindDuplicateProperty(property.Value, childLocation, out duplicateLocation))
                {
                    duplicateLocation ??= childLocation;
                    return true;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (TryFindDuplicateProperty(item, $"{location}[{index}]", out duplicateLocation))
                {
                    return true;
                }

                index++;
            }
        }

        return false;
    }

    private sealed record ParseResult<T>(T? Value, GroupQuotaEventV1DecodeFailure? Failure)
        where T : class;

    private sealed record EnvelopeDiscriminator(
        string Topic,
        int SchemaVersion,
        string EventType);

    private sealed record EnvelopePosition(
        EntityId MessageId,
        long EventSequence,
        long SourceEventSequence,
        string AggregateType,
        EntityId AggregateId);

    private sealed record EnvelopeContext(
        string DeduplicationKey,
        DateTimeOffset OccurredAt,
        EntityId CorrelationId,
        EntityId? CausationId,
        EntityId? ReplayOf);

    private sealed record PayloadDiscriminator(
        EntityId EventId,
        string EventType,
        long SourceEventSequence);

    private sealed record PayloadIdentity(
        EntityId CorrelationId,
        EntityId? CausationId,
        EntityId GroupId,
        EntityId PeriodId,
        EntityId? ReservationId,
        EntityId? AttemptId);

    private sealed record PayloadState(
        BigInteger DeltaTotalTokens,
        BigInteger DeltaConsumedTokens,
        BigInteger DeltaReservedTokens,
        BigInteger TotalTokens,
        BigInteger ConsumedTokens,
        BigInteger ReservedTokens,
        DateTimeOffset OccurredAt,
        JsonElement Metadata);
}
