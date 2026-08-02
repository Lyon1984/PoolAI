using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.UnitTests;

// Governing contract: docs/database/README.md §8 and the canonical
// docs/contracts/group-quota-events-v1.json Published Language.
public sealed class GroupQuotaEventV1CodecTests
{
    [Fact]
    public void EveryAuthoritativePositiveFixtureDecodesToTheClosedUnion()
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-valid.json");
        HashSet<string> eventTypes = new(StringComparer.Ordinal);

        foreach (JsonElement item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement serializedEnvelope = item.GetProperty("envelope");
            GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
                serializedEnvelope);

            Assert.True(
                result.IsSuccess,
                $"{item.GetProperty("name").GetString()}: {result.Failure}");
            GroupQuotaEventEnvelopeV1 envelope = Assert.IsType<GroupQuotaEventEnvelopeV1>(
                result.Envelope);
            Assert.Null(result.Failure);

            string expectedEventType = item.GetProperty("expected_event_type").GetString()!;
            eventTypes.Add(expectedEventType);
            Assert.Equal(expectedEventType, envelope.EventType);
            Assert.Equal(expectedEventType, envelope.Payload.EventType);
            Assert.Equal(
                item.GetProperty("expected_runtime_type").GetString(),
                envelope.Payload.GetType().Name);
            Assert.Equal(
                Enum.Parse<GroupQuotaUsageProjectionDisposition>(
                    item.GetProperty("expected_usage_projection").GetString()!),
                envelope.Payload.UsageProjection);

            Assert.Equal(
                serializedEnvelope.GetProperty("event_sequence").GetInt64(),
                envelope.EventSequence);
            Assert.Equal(
                serializedEnvelope.GetProperty("source_event_sequence").GetInt64(),
                envelope.SourceEventSequence);
            Assert.Equal(envelope.SourceEventSequence, envelope.Payload.Data.SourceEventSequence);
            Assert.Equal(envelope.AggregateId, envelope.Payload.Data.GroupId);
            Assert.Equal(envelope.CorrelationId, envelope.Payload.Data.CorrelationId);
            Assert.Equal(envelope.CausationId, envelope.Payload.Data.CausationId);
            Assert.Equal(envelope.OccurredAt, envelope.Payload.Data.OccurredAt);

            if (item.TryGetProperty(
                    "expected_conservative_expiry",
                    out JsonElement expectedConservativeExpiry))
            {
                GroupQuotaExpiredEventV1 expired = Assert.IsType<GroupQuotaExpiredEventV1>(
                    envelope.Payload);
                Assert.Equal(expectedConservativeExpiry.GetBoolean(), expired.ConservativeExpiry);
            }
        }

        Assert.Equal(10, eventTypes.Count);
    }

    [Fact]
    public void EveryAuthoritativeNegativeFixtureFailsWithItsStableFailureClass()
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-invalid.json");
        JsonNode baseEnvelope = JsonNode.Parse(
            fixtures.RootElement.GetProperty("base_envelope").GetRawText())!;

        foreach (JsonElement item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonNode candidate = baseEnvelope.DeepClone();
            foreach (JsonElement mutation in item.GetProperty("mutations").EnumerateArray())
            {
                ApplyMutation(candidate, mutation);
            }

            GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
                candidate.ToJsonString());

            Assert.False(result.IsSuccess, item.GetProperty("name").GetString());
            Assert.Null(result.Envelope);
            GroupQuotaEventV1DecodeFailure failure =
                Assert.IsType<GroupQuotaEventV1DecodeFailure>(result.Failure);
            Assert.Equal(
                Enum.Parse<GroupQuotaEventV1DecodeFailureCode>(
                    item.GetProperty("expected_failure").GetString()!),
                failure.Code);
        }
    }

    [Fact]
    public void CodecAcceptsAdditiveOptionalFieldsButRejectsDuplicateProperties()
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-valid.json");
        JsonElement additive = Assert.Single(
            fixtures.RootElement.GetProperty("cases").EnumerateArray(),
            static item => string.Equals(
                item.GetProperty("name").GetString(),
                "initialized_accepts_unknown_optional_fields",
                StringComparison.Ordinal));

        GroupQuotaEventV1DecodeResult additiveResult = GroupQuotaEventV1Codec.Decode(
            additive.GetProperty("envelope"));
        Assert.True(additiveResult.IsSuccess);

        GroupQuotaEventV1DecodeResult duplicate = GroupQuotaEventV1Codec.Decode(
            "{\"topic\":\"poolai.quota.v1\",\"topic\":\"poolai.quota.v1\"}");
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
            duplicate.Failure?.Code);
        Assert.Equal("$.topic", duplicate.Failure?.Location);

        GroupQuotaEventV1DecodeResult malformed = GroupQuotaEventV1Codec.Decode("{");
        Assert.False(malformed.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.MalformedJson,
            malformed.Failure?.Code);
    }

    [Fact]
    public void CodecPreservesReplayPhysicalPositionWithoutChangingLogicalSourcePosition()
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-valid.json");
        JsonElement settled = Assert.Single(
            fixtures.RootElement.GetProperty("cases").EnumerateArray(),
            static item => string.Equals(
                item.GetProperty("expected_event_type").GetString(),
                "settled",
                StringComparison.Ordinal));
        JsonNode replay = JsonNode.Parse(settled.GetProperty("envelope").GetRawText())!;

        ((JsonObject)replay)["event_sequence"] = 4_205;
        ((JsonObject)replay)["replay_of"] = "0190f8bf-a040-7444-a2ca-c4bc32e48f01";

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            replay.ToJsonString());

        GroupQuotaEventEnvelopeV1 envelope = Assert.IsType<GroupQuotaEventEnvelopeV1>(
            result.Envelope);
        Assert.Equal(4_205, envelope.EventSequence);
        Assert.Equal(5, envelope.SourceEventSequence);
        Assert.Equal(5, envelope.Payload.Data.SourceEventSequence);
        Assert.Equal(
            Guid.Parse("0190f8bf-a040-7444-a2ca-c4bc32e48f01"),
            envelope.ReplayOf?.Value);
    }

    [Fact]
    public void CodecEnforcesLosslessNumeric78DecimalStrings()
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-valid.json");
        JsonElement initialized = Assert.Single(
            fixtures.RootElement.GetProperty("cases").EnumerateArray(),
            static item => string.Equals(
                item.GetProperty("expected_event_type").GetString(),
                "initialized",
                StringComparison.Ordinal));
        JsonNode maximum = JsonNode.Parse(initialized.GetProperty("envelope").GetRawText())!;
        JsonObject payload = (JsonObject)maximum["payload"]!;
        string maximum78 = new('9', 78);
        payload["delta_total_tokens"] = maximum78;
        payload["total_tokens"] = maximum78;

        GroupQuotaEventV1DecodeResult maximumResult = GroupQuotaEventV1Codec.Decode(
            maximum.ToJsonString());
        Assert.True(maximumResult.IsSuccess);
        Assert.Equal(
            BigInteger.Parse(maximum78, System.Globalization.CultureInfo.InvariantCulture),
            maximumResult.Envelope?.Payload.Data.TotalTokens);

        payload["total_tokens"] = new string('9', 79);
        GroupQuotaEventV1DecodeResult overflow = GroupQuotaEventV1Codec.Decode(
            maximum.ToJsonString());
        Assert.False(overflow.IsSuccess);
        Assert.Equal(GroupQuotaEventV1DecodeFailureCode.InvalidPayload, overflow.Failure?.Code);

        payload["total_tokens"] = "01";
        GroupQuotaEventV1DecodeResult nonCanonical = GroupQuotaEventV1Codec.Decode(
            maximum.ToJsonString());
        Assert.False(nonCanonical.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidPayload,
            nonCanonical.Failure?.Code);
    }

    [Theory]
    [MemberData(nameof(MalformedKnownFieldCases))]
    public void CodecRejectsEveryMalformedKnownFieldAtItsStableLocation(
        string operation,
        string path,
        string serializedValue,
        GroupQuotaEventV1DecodeFailureCode expectedCode,
        string expectedLocation)
    {
        JsonNode candidate = ReadValidEnvelope("settled");
        ApplyMutation(candidate, operation, path, serializedValue);

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            candidate.ToJsonString());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Envelope);
        GroupQuotaEventV1DecodeFailure failure =
            Assert.IsType<GroupQuotaEventV1DecodeFailure>(result.Failure);
        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedLocation, failure.Location);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-an-envelope\"")]
    [InlineData("1")]
    [InlineData("true")]
    public void CodecRejectsEveryNonObjectEnvelopeRoot(string json)
    {
        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(json);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
            result.Failure?.Code);
        Assert.Equal("$", result.Failure?.Location);
    }

    [Theory]
    [InlineData("2026-08-02T09:00:05+08:00", 8)]
    [InlineData("2026-08-01T20:00:05-05:00", -5)]
    public void CodecAcceptsExplicitNumericTimestampOffsets(
        string timestamp,
        int expectedOffsetHours)
    {
        JsonNode candidate = ReadValidEnvelope("settled");
        ((JsonObject)candidate)["occurred_at"] = timestamp;
        ((JsonObject)candidate["payload"]!)["occurred_at"] = timestamp;

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            candidate.ToJsonString());

        GroupQuotaEventEnvelopeV1 envelope = Assert.IsType<GroupQuotaEventEnvelopeV1>(
            result.Envelope);
        Assert.Null(result.Failure);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), envelope.OccurredAt.Offset);
        Assert.Equal(envelope.OccurredAt, envelope.Payload.Data.OccurredAt);
    }

    [Theory]
    [InlineData("0190f8bf-a040-1444-82ca-c4bc32e48105")]
    [InlineData("0190f8bf-a040-8444-b2ca-c4bc32e48105")]
    public void CodecAcceptsTheClosedUuidVersionAndVariantBoundaries(string messageId)
    {
        JsonNode candidate = ReadValidEnvelope("settled");
        ((JsonObject)candidate)["message_id"] = messageId;

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            candidate.ToJsonString());

        GroupQuotaEventEnvelopeV1 envelope = Assert.IsType<GroupQuotaEventEnvelopeV1>(
            result.Envelope);
        Assert.Null(result.Failure);
        Assert.Equal(Guid.Parse(messageId), envelope.MessageId.Value);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0190F8BF-A040-7444-A2CA-C4BC32E48105")]
    [InlineData("0190f8bf-a040-9444-a2ca-c4bc32e48105")]
    [InlineData("0190f8bf-a040-7444-c2ca-c4bc32e48105")]
    public void CodecRejectsEveryNonCanonicalUuidClass(string messageId)
    {
        JsonNode candidate = ReadValidEnvelope("settled");
        ((JsonObject)candidate)["message_id"] = messageId;

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            candidate.ToJsonString());

        Assert.False(result.IsSuccess);
        Assert.Equal(GroupQuotaEventV1DecodeFailureCode.MissingIdentity, result.Failure?.Code);
        Assert.Equal("$.message_id", result.Failure?.Location);
    }

    [Fact]
    public void CodecAcceptsBothSignedAndUnsignedSeventyEightDigitTokenBoundaries()
    {
        JsonNode candidate = ReadValidEnvelope("settled");
        JsonObject payload = (JsonObject)candidate["payload"]!;
        string maximum78 = new('9', 78);
        payload["delta_total_tokens"] = $"-{maximum78}";
        payload["total_tokens"] = maximum78;

        GroupQuotaEventV1DecodeResult result = GroupQuotaEventV1Codec.Decode(
            candidate.ToJsonString());

        GroupQuotaEventEnvelopeV1 envelope = Assert.IsType<GroupQuotaEventEnvelopeV1>(
            result.Envelope);
        Assert.Null(result.Failure);
        Assert.Equal(
            BigInteger.Parse(
                $"-{maximum78}",
                System.Globalization.CultureInfo.InvariantCulture),
            envelope.Payload.Data.DeltaTotalTokens);
        Assert.Equal(
            BigInteger.Parse(maximum78, System.Globalization.CultureInfo.InvariantCulture),
            envelope.Payload.Data.TotalTokens);
    }

    [Fact]
    public void ConservativeExpiryRequiresBothImmutableAttemptFactIdentifiers()
    {
        JsonNode missingReservation = ReadValidEnvelope("expired", conservativeExpiry: true);
        Assert.True(((JsonObject)missingReservation["payload"]!).Remove("reservation_id"));

        GroupQuotaEventV1DecodeResult reservationResult = GroupQuotaEventV1Codec.Decode(
            missingReservation.ToJsonString());

        Assert.False(reservationResult.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.MissingIdentity,
            reservationResult.Failure?.Code);
        Assert.Equal("$.payload.reservation_id", reservationResult.Failure?.Location);

        JsonNode invalidFlag = ReadValidEnvelope("expired", conservativeExpiry: true);
        ((JsonObject)invalidFlag["payload"]!["metadata"]!)["conservative_expiry"] = "true";

        GroupQuotaEventV1DecodeResult flagResult = GroupQuotaEventV1Codec.Decode(
            invalidFlag.ToJsonString());

        Assert.False(flagResult.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidEventSemantics,
            flagResult.Failure?.Code);
        Assert.Equal("$.payload.metadata.conservative_expiry", flagResult.Failure?.Location);
    }

    [Fact]
    public void CodecTraversesAdditiveArraysAndRejectsNestedDuplicateProperties()
    {
        JsonNode additive = ReadValidEnvelope("settled");
        ((JsonObject)additive["payload"]!)["extensions"] = new JsonArray(
            1,
            new JsonObject { ["accepted"] = true });

        GroupQuotaEventV1DecodeResult additiveResult = GroupQuotaEventV1Codec.Decode(
            additive.ToJsonString());
        Assert.True(additiveResult.IsSuccess);

        string nestedObjectDuplicate = ReadValidEnvelope("settled")
            .ToJsonString()
            .Replace(
                "\"usage_source\":\"upstream\"",
                "\"usage_source\":\"upstream\",\"usage_source\":\"upstream\"",
                StringComparison.Ordinal);
        GroupQuotaEventV1DecodeResult objectResult = GroupQuotaEventV1Codec.Decode(
            nestedObjectDuplicate);
        Assert.False(objectResult.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
            objectResult.Failure?.Code);
        Assert.Equal("$.payload.metadata.usage_source", objectResult.Failure?.Location);

        string nestedArrayDuplicate = ReadValidEnvelope("settled")
            .ToJsonString()
            .Replace(
                "\"metadata\":{\"usage_source\":\"upstream\"}",
                "\"metadata\":{\"usage_source\":\"upstream\"},"
                    + "\"extensions\":[{\"name\":\"a\",\"name\":\"b\"}]",
                StringComparison.Ordinal);
        GroupQuotaEventV1DecodeResult arrayResult = GroupQuotaEventV1Codec.Decode(
            nestedArrayDuplicate);
        Assert.False(arrayResult.IsSuccess);
        Assert.Equal(
            GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope,
            arrayResult.Failure?.Code);
        Assert.Equal("$.payload.extensions[0].name", arrayResult.Failure?.Location);
    }

    public static TheoryData<
        string,
        string,
        string,
        GroupQuotaEventV1DecodeFailureCode,
        string> MalformedKnownFieldCases => new()
    {
        { "remove", "/topic", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.topic" },
        { "replace", "/topic", "1", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.topic" },
        { "replace", "/topic", "\"poolai.quota.v2\"", GroupQuotaEventV1DecodeFailureCode.UnsupportedTopic, "$.topic" },
        { "remove", "/schema_version", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.schema_version" },
        { "replace", "/schema_version", "\"1\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.schema_version" },
        { "replace", "/schema_version", "2147483648", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.schema_version" },
        { "remove", "/event_type", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.event_type" },
        { "replace", "/event_type", "null", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.event_type" },
        { "replace", "/message_id", "7", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.message_id" },
        { "remove", "/event_sequence", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.event_sequence" },
        { "replace", "/event_sequence", "0", GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.event_sequence" },
        { "replace", "/event_sequence", "\"105\"", GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.event_sequence" },
        { "replace", "/event_sequence", "9223372036854775808", GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.event_sequence" },
        { "replace", "/source_event_sequence", "1.5", GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.source_event_sequence" },
        { "remove", "/aggregate_type", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.aggregate_type" },
        { "replace", "/aggregate_type", "\"account\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.aggregate_type" },
        { "replace", "/aggregate_id", "false", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.aggregate_id" },
        { "remove", "/aggregate_version", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.aggregate_version" },
        { "replace", "/aggregate_version", "1", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.aggregate_version" },
        { "remove", "/deduplication_key", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.deduplication_key" },
        { "replace", "/deduplication_key", "\" \\t\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.deduplication_key" },
        { "replace", "/occurred_at", "\"2026-08-02T01:00:05\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.occurred_at" },
        { "replace", "/occurred_at", "\"2026-08-02Z\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.occurred_at" },
        { "replace", "/occurred_at", "\"2026-02-30T01:00:05Z\"", GroupQuotaEventV1DecodeFailureCode.InvalidEnvelope, "$.occurred_at" },
        { "replace", "/correlation_id", "null", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.correlation_id" },
        { "remove", "/causation_id", string.Empty, GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.causation_id" },
        { "replace", "/causation_id", "\"not-a-guid\"", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.causation_id" },
        { "remove", "/replay_of", string.Empty, GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.replay_of" },
        { "replace", "/replay_of", "0", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.replay_of" },
        { "remove", "/payload", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload", "[]", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "remove", "/payload/schema_version", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.schema_version" },
        { "replace", "/payload/schema_version", "\"1\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.schema_version" },
        { "replace", "/payload/schema_version", "2", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.schema_version" },
        { "remove", "/payload/event_type", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.event_type" },
        { "replace", "/payload/event_type", "false", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.event_type" },
        { "replace", "/payload/event_type", "\"future_type\"", GroupQuotaEventV1DecodeFailureCode.UnsupportedEventType, "$.payload.event_type" },
        { "replace", "/payload/event_type", "\"released\"", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.event_type" },
        { "replace", "/payload/event_id", "\"not-a-guid\"", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.event_id" },
        { "replace", "/payload/source_event_sequence", "0", GroupQuotaEventV1DecodeFailureCode.InvalidSequence, "$.payload.source_event_sequence" },
        { "replace", "/payload/correlation_id", "17", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.correlation_id" },
        { "replace", "/payload/causation_id", "\"not-a-guid\"", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.causation_id" },
        { "replace", "/payload/group_id", "null", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.group_id" },
        { "replace", "/payload/period_id", "\"\"", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.period_id" },
        { "replace", "/payload/reservation_id", "3", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.reservation_id" },
        { "replace", "/payload/attempt_id", "true", GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.attempt_id" },
        { "remove", "/payload/reservation_id", string.Empty, GroupQuotaEventV1DecodeFailureCode.MissingIdentity, "$.payload.reservation_id" },
        { "replace", "/payload/delta_total_tokens", "null", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/delta_consumed_tokens", "\"\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/delta_reserved_tokens", "\"-\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/delta_total_tokens", "\"-0\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/delta_total_tokens", "\"+1\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/total_tokens", "\"-1\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/consumed_tokens", "\"01\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/consumed_tokens", "\"1e2\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/reserved_tokens", "\"9999999999999999999999999999999999999999999999999999999999999999999999999999999\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload" },
        { "replace", "/payload/occurred_at", "\"2026-08-02 01:00:05Z\"", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.occurred_at" },
        { "remove", "/payload/metadata", string.Empty, GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.metadata" },
        { "replace", "/payload/metadata", "[]", GroupQuotaEventV1DecodeFailureCode.InvalidPayload, "$.payload.metadata" },
        { "replace", "/payload/source_event_sequence", "6", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.source_event_sequence" },
        { "replace", "/payload/group_id", "\"0190f8bf-a040-7444-a2ca-c4bc32e48e09\"", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.group_id" },
        { "replace", "/payload/correlation_id", "\"0190f8bf-a040-7444-a2ca-c4bc32e48d09\"", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.correlation_id" },
        { "replace", "/payload/causation_id", "null", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.causation_id" },
        { "replace", "/payload/occurred_at", "\"2026-08-02T01:00:06Z\"", GroupQuotaEventV1DecodeFailureCode.EnvelopePayloadMismatch, "$.payload.occurred_at" },
    };

    private static JsonDocument ReadFixture(string fixtureName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "fixtures",
            fixtureName)));

    private static JsonNode ReadValidEnvelope(
        string eventType,
        bool? conservativeExpiry = null)
    {
        using JsonDocument fixtures = ReadFixture("group-quota-events-v1-valid.json");
        JsonElement item = Assert.Single(
            fixtures.RootElement.GetProperty("cases").EnumerateArray(),
            candidate => string.Equals(
                    candidate.GetProperty("expected_event_type").GetString(),
                    eventType,
                    StringComparison.Ordinal)
                && (conservativeExpiry is null
                    || candidate.GetProperty("expected_conservative_expiry").GetBoolean()
                        == conservativeExpiry));
        return JsonNode.Parse(item.GetProperty("envelope").GetRawText())!;
    }

    private static void ApplyMutation(JsonNode root, JsonElement mutation)
    {
        string operation = mutation.GetProperty("op").GetString()!;
        string path = mutation.GetProperty("path").GetString()!;
        string serializedValue = mutation.TryGetProperty("value", out JsonElement value)
            ? value.GetRawText()
            : string.Empty;
        ApplyMutation(root, operation, path, serializedValue);
    }

    private static void ApplyMutation(
        JsonNode root,
        string operation,
        string path,
        string serializedValue)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapeJsonPointerSegment)
            .ToArray();
        Assert.NotEmpty(segments);

        JsonNode parent = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            parent = parent[segments[index]]
                ?? throw new InvalidOperationException($"Fixture mutation parent is missing: {path}");
        }

        JsonObject target = Assert.IsType<JsonObject>(parent);
        string propertyName = segments[^1];
        switch (operation)
        {
            case "remove":
                Assert.True(target.Remove(propertyName), $"Fixture mutation target is missing: {path}");
                break;
            case "add":
            case "replace":
                if (string.Equals(operation, "replace", StringComparison.Ordinal))
                {
                    Assert.True(target.ContainsKey(propertyName), $"Fixture mutation target is missing: {path}");
                }

                target[propertyName] = JsonNode.Parse(
                    serializedValue);
                break;
            default:
                throw new InvalidOperationException($"Unsupported fixture mutation operation: {operation}");
        }
    }

    private static string UnescapeJsonPointerSegment(string value) =>
        value.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PoolAI.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PoolAI repository root.");
    }
}
