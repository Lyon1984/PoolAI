using PoolAI.Modules.Supply.Infrastructure.Persistence;

namespace PoolAI.UnitTests;

public sealed class SupplyReadinessEvidenceTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 30, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void EquivalentObjectsProduceTheSameOpaqueV1Token()
    {
        string first = SupplyReadinessEvidence.Create(
            """{"ready":true,"v":1,"nested":{"b":2,"a":"x"}}""",
            ObservedAt);
        string second = SupplyReadinessEvidence.Create(
            """{"nested":{"a":"x","b":2},"v":1,"ready":true}""",
            ObservedAt);

        Assert.Equal(first, second);
        Assert.Matches("^v1\\.[A-Za-z0-9_-]{43}$", first);
        Assert.DoesNotContain("ready", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayOrderAndValueChangesAreCoveredByTheEvidence()
    {
        string ordered = SupplyReadinessEvidence.Create(
            """{"v":1,"bindings":[{"id":"a"},{"id":"b"}]}""",
            ObservedAt);
        string reversed = SupplyReadinessEvidence.Create(
            """{"v":1,"bindings":[{"id":"b"},{"id":"a"}]}""",
            ObservedAt);
        string changed = SupplyReadinessEvidence.Create(
            """{"v":1,"bindings":[{"id":"a"},{"id":"c"}]}""",
            ObservedAt);

        Assert.NotEqual(ordered, reversed);
        Assert.NotEqual(ordered, changed);
    }

    [Fact]
    public void DatabaseObservationTimeIsCoveredByTheEvidence()
    {
        string first = SupplyReadinessEvidence.Create(
            """{"v":1,"ready":true}""",
            ObservedAt);
        string later = SupplyReadinessEvidence.Create(
            """{"v":1,"ready":true}""",
            ObservedAt.AddTicks(10));

        Assert.NotEqual(first, later);
    }

    [Theory]
    [InlineData("""{"credential":"value"}""")]
    [InlineData("""{"credential_prefix":"sha256:abc"}""")]
    [InlineData("""{"upstream_base_url":"https://secret.invalid"}""")]
    [InlineData("""{"api-key":"value"}""")]
    [InlineData("""{"nested":{"authorization":"Bearer value"}}""")]
    [InlineData("""{"password":"value"}""")]
    [InlineData("""{"secret_value":"value"}""")]
    public void SecretAndBaseUrlFieldsAreRejected(string json)
    {
        Assert.Throws<InvalidOperationException>(
            () => SupplyReadinessEvidence.Create(json, ObservedAt));
    }

    [Fact]
    public void NonObjectBlankAndUnsupportedJsonAreRejected()
    {
        Assert.Throws<ArgumentException>(
            () => SupplyReadinessEvidence.Create(" ", ObservedAt));
        Assert.Throws<InvalidOperationException>(
            () => SupplyReadinessEvidence.Create(
                """["not","an","object"]""",
                ObservedAt));
    }

    [Fact]
    public void CanonicalWriterCoversAllSupportedJsonKinds()
    {
        string token = SupplyReadinessEvidence.Create(
            """
            {
              "string":"value",
              "number":1.25,
              "true":true,
              "false":false,
              "null":null,
              "array":[1,"two",false,null]
            }
            """,
            ObservedAt);

        Assert.StartsWith("v1.", token, StringComparison.Ordinal);
    }
}
