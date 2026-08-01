using System.Text.Json;

namespace PoolAI.ContractTests;

// Governing contract: docs/database/README.md §8.
public sealed class GroupQuotaEventContractTests
{
    private static readonly string[] EventTypes =
    [
        "initialized",
        "reserved",
        "dispatch_started",
        "renewed",
        "settled",
        "released",
        "expired",
        "usage_adjusted",
        "total_adjusted",
        "period_reset",
    ];

    private static readonly string[] RequiredEnvelopeFields =
    [
        "message_id",
        "topic",
        "event_type",
        "schema_version",
        "event_sequence",
        "source_event_sequence",
        "aggregate_type",
        "aggregate_id",
        "aggregate_version",
        "deduplication_key",
        "occurred_at",
        "correlation_id",
        "causation_id",
        "payload",
        "replay_of",
    ];

    [Fact]
    public void MachineSchemaFreezesCompleteEnvelopeUnionAndAdditiveV1Compatibility()
    {
        string root = FindRepositoryRoot();
        string schemaPath = Path.Combine(
            root,
            "docs",
            "contracts",
            "group-quota-events-v1.json");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        JsonElement schema = document.RootElement;

        Assert.Equal("poolai.quota.v1", schema.GetProperty("x-poolai-topic").GetString());
        Assert.Equal(1, schema.GetProperty("x-poolai-schema-version").GetInt32());
        AssertEnvelopeShape(schema);
        AssertCompatibilityPolicy(schema);
        AssertEventMapping(schema);
    }

    private static void AssertEnvelopeShape(JsonElement schema)
    {
        Assert.True(schema.GetProperty("additionalProperties").GetBoolean());

        string[] required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        Assert.Equal(
            RequiredEnvelopeFields.Order(StringComparer.Ordinal),
            required.Order(StringComparer.Ordinal));

        JsonElement properties = schema.GetProperty("properties");
        Assert.Contains("Physical Outbox", properties.GetProperty("event_sequence")
            .GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Contains("Stable GroupQuota ledger", properties.GetProperty("source_event_sequence")
            .GetProperty("description").GetString()!, StringComparison.Ordinal);
        Assert.Equal(
            10,
            properties.GetProperty("payload").GetProperty("oneOf").GetArrayLength());
    }

    private static void AssertCompatibilityPolicy(JsonElement schema)
    {
        JsonElement compatibility = schema.GetProperty("x-poolai-compatibility");
        Assert.Equal(
            "accept",
            compatibility.GetProperty("unknown_optional_envelope_fields").GetString());
        Assert.Equal(
            "accept",
            compatibility.GetProperty("unknown_optional_payload_fields").GetString());
        Assert.Equal(
            "accept",
            compatibility.GetProperty("unknown_optional_metadata_fields").GetString());
        Assert.Equal("reject", compatibility.GetProperty("unknown_schema_versions").GetString());
        Assert.Equal("reject", compatibility.GetProperty("unknown_event_types").GetString());
    }

    private static void AssertEventMapping(JsonElement schema)
    {
        string[] schemaEventTypes = schema.GetProperty("$defs")
            .GetProperty("eventType")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();
        Assert.Equal(
            EventTypes.Order(StringComparer.Ordinal),
            schemaEventTypes.Order(StringComparer.Ordinal));

        JsonElement mapping = schema.GetProperty("x-poolai-event-type-mapping");
        Assert.Equal(
            EventTypes.Order(StringComparer.Ordinal),
            mapping.EnumerateObject()
                .Select(static item => item.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            "rebuild_attempt_hour",
            mapping.GetProperty("settled").GetProperty("usage_projection").GetString());
        Assert.Equal(
            "rebuild_attempt_hour",
            mapping.GetProperty("usage_adjusted").GetProperty("usage_projection").GetString());
        Assert.Equal(
            "rebuild_attempt_hour_when_conservative_expiry_true",
            mapping.GetProperty("expired").GetProperty("usage_projection").GetString());
    }

    [Fact]
    public void AuthoritativeFixturesCoverEveryEventTypeBothExpiryModesAndFailureClasses()
    {
        string fixtureRoot = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "contracts",
            "fixtures");

        using JsonDocument valid = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            fixtureRoot,
            "group-quota-events-v1-valid.json")));
        JsonElement[] validCases = valid.RootElement.GetProperty("cases")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(11, validCases.Length);
        Assert.Equal(
            EventTypes.Order(StringComparer.Ordinal),
            validCases.Select(static item => item.GetProperty("expected_event_type").GetString()!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

        JsonElement[] expired = validCases
            .Where(static item =>
                string.Equals(
                    item.GetProperty("expected_event_type").GetString(),
                    "expired",
                    StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, expired.Length);
        Assert.Contains(expired, static item =>
            item.GetProperty("expected_conservative_expiry").GetBoolean());
        Assert.Contains(expired, static item =>
            !item.GetProperty("expected_conservative_expiry").GetBoolean());

        JsonElement additive = Assert.Single(validCases, static item =>
            string.Equals(
                item.GetProperty("name").GetString(),
                "initialized_accepts_unknown_optional_fields",
                StringComparison.Ordinal));
        Assert.True(additive.GetProperty("envelope")
            .TryGetProperty("future_optional_envelope", out _));
        Assert.True(additive.GetProperty("envelope")
            .GetProperty("payload")
            .TryGetProperty("future_optional_payload", out _));

        AssertNegativeFixtures(fixtureRoot);
    }

    private static void AssertNegativeFixtures(string fixtureRoot)
    {
        using JsonDocument invalid = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            fixtureRoot,
            "group-quota-events-v1-invalid.json")));
        JsonElement[] invalidCases = invalid.RootElement.GetProperty("cases")
            .EnumerateArray()
            .ToArray();
        string[] names = invalidCases
            .Select(static item => item.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Contains("unknown_major", names);
        Assert.Contains("unknown_event_type", names);
        Assert.Contains("envelope_payload_event_type_mismatch", names);
        Assert.Contains("envelope_payload_source_sequence_mismatch", names);
        Assert.Contains("missing_message_id", names);
        Assert.Contains("missing_settled_attempt_id", names);
        Assert.Contains("missing_outer_source_event_sequence", names);
        Assert.Contains("missing_payload_source_event_sequence", names);
        Assert.Contains("expired_missing_conservative_expiry", names);
        Assert.Contains("conservative_expired_missing_attempt_id", names);
    }

    [Fact]
    public void GroupQuotaContractAssetsExistOnlyUnderDocumentedContractArea()
    {
        string root = FindRepositoryRoot();
        string schemaName = "group-quota-events-v1.json";
        string[] fixtureNames =
        [
            "group-quota-events-v1-valid.json",
            "group-quota-events-v1-invalid.json",
        ];

        Assert.True(File.Exists(Path.Combine(root, "docs", "contracts", schemaName)));
        foreach (string fixtureName in fixtureNames)
        {
            Assert.True(File.Exists(Path.Combine(
                root,
                "docs",
                "contracts",
                "fixtures",
                fixtureName)));
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), schemaName, SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "tests"), schemaName, SearchOption.AllDirectories));
        foreach (string fixtureName in fixtureNames)
        {
            Assert.Empty(Directory.GetFiles(
                Path.Combine(root, "src"),
                fixtureName,
                SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(
                Path.Combine(root, "tests"),
                fixtureName,
                SearchOption.AllDirectories));
        }
    }

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
