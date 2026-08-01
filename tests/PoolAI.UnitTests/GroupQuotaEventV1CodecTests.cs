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

    private static JsonDocument ReadFixture(string fixtureName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "fixtures",
            fixtureName)));

    private static void ApplyMutation(JsonNode root, JsonElement mutation)
    {
        string operation = mutation.GetProperty("op").GetString()!;
        string path = mutation.GetProperty("path").GetString()!;
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
                    mutation.GetProperty("value").GetRawText());
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
