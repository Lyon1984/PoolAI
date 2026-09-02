using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using PoolAI.BuildingBlocks;
using PoolAI.Modules.Gateway.Abstractions;
using PoolAI.Modules.Gateway.Application;

namespace PoolAI.UnitTests;

// Governing contract: ADR 0015, conservative-v1 and lossless usage.
public sealed class GatewayConservativeEstimatorTests
{
    private static readonly JsonSerializerOptions FrozenCompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
        WriteIndented = false,
    };

    [Theory]
    [InlineData(InboundProtocol.Responses, "max_output_tokens")]
    [InlineData(InboundProtocol.ChatCompletions, "max_completion_tokens")]
    public void CompactUtf8EstimateUsesTheFrozenDefaultAndExplicitField(
        InboundProtocol protocol,
        string outputProperty)
    {
        using JsonDocument defaultDocument = JsonDocument.Parse(
            "{\"model\":\"gpt-4.1\",\"input\":\"hello\"}");
        ConservativeTokenEstimator estimator = new(new GatewayEstimationOptions());

        Result<GatewayTokenEstimate> defaultResult = estimator.Estimate(
            protocol,
            defaultDocument.RootElement);

        Assert.True(defaultResult.IsSuccess);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(defaultDocument.RootElement.GetRawText()) + 64,
            defaultResult.Value.InputTokens);
        Assert.Equal(4_096, defaultResult.Value.OutputTokens);

        using JsonDocument explicitDocument = JsonDocument.Parse(
            $"{{\"model\":\"gpt-4.1\",\"{outputProperty}\":2.5e2}}");
        Result<GatewayTokenEstimate> explicitResult = estimator.Estimate(
            protocol,
            explicitDocument.RootElement);

        Assert.True(explicitResult.IsSuccess);
        Assert.Equal(250, explicitResult.Value.OutputTokens);
        Assert.Equal(
            explicitResult.Value.InputTokens + 250,
            explicitResult.Value.TotalTokens);
    }

    [Theory]
    [InlineData("{ \"input\" : \"a\\/b\", \"escaped\":\"<\\u00e9\\n\" }")]
    [InlineData("{\"input\":\"😀\",\"number\":1.00e+02}")]
    public void CountOnlySerializationMatchesTheFrozenCompactEncoding(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        int expectedLength = JsonSerializer.SerializeToUtf8Bytes(
            document.RootElement,
            FrozenCompactJson).Length;
        ConservativeTokenEstimator estimator = new(new GatewayEstimationOptions());

        Result<GatewayTokenEstimate> result = estimator.Estimate(
            InboundProtocol.Responses,
            document.RootElement);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedLength + 64, result.Value.InputTokens);
    }

    [Theory]
    [InlineData(InboundProtocol.Responses, "max_output_tokens")]
    [InlineData(InboundProtocol.ChatCompletions, "max_completion_tokens")]
    public void InvalidOrOutputCausedExcessPointsAtTheSubmittedLimit(
        InboundProtocol protocol,
        string outputProperty)
    {
        ConservativeTokenEstimator estimator = new(
            new GatewayEstimationOptions(
                defaultMaxOutputTokens: 10,
                maximumEstimatedTokensPerAttempt: 200));
        using JsonDocument invalid = JsonDocument.Parse(
            $"{{\"{outputProperty}\":-1}}");
        using JsonDocument excessive = JsonDocument.Parse(
            $"{{\"{outputProperty}\":150}}");

        Result<GatewayTokenEstimate> invalidResult = estimator.Estimate(
            protocol,
            invalid.RootElement);
        Result<GatewayTokenEstimate> excessiveResult = estimator.Estimate(
            protocol,
            excessive.RootElement);

        AssertValidationPointer(invalidResult, $"/{outputProperty}");
        AssertValidationPointer(excessiveResult, $"/{outputProperty}");
    }

    [Theory]
    [InlineData(InboundProtocol.Responses, "/input")]
    [InlineData(InboundProtocol.ChatCompletions, "/messages")]
    public void InputOnlyExcessUsesTheProtocolInputPointer(
        InboundProtocol protocol,
        string expectedPointer)
    {
        ConservativeTokenEstimator estimator = new(
            new GatewayEstimationOptions(
                defaultMaxOutputTokens: 1,
                maximumEstimatedTokensPerAttempt: 100));
        using JsonDocument document = JsonDocument.Parse(
            $"{{\"payload\":\"{new string('x', 80)}\"}}");

        Result<GatewayTokenEstimate> result = estimator.Estimate(
            protocol,
            document.RootElement);

        AssertValidationPointer(result, expectedPointer);
    }

    [Fact]
    public void OptionsRejectDefaultAboveThePerAttemptMaximum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GatewayEstimationOptions(
                defaultMaxOutputTokens: 101,
                maximumEstimatedTokensPerAttempt: 100));
    }

    [Fact]
    public void OversizedNumericLimitFailsClosedWithoutStackAllocation()
    {
        string oversizedNumber = string.Concat("1", new string('0', 100_000));
        using JsonDocument document = JsonDocument.Parse(
            $"{{\"max_output_tokens\":{oversizedNumber}}}");
        ConservativeTokenEstimator estimator = new(new GatewayEstimationOptions());

        Result<GatewayTokenEstimate> result = estimator.Estimate(
            InboundProtocol.Responses,
            document.RootElement);

        AssertValidationPointer(result, "/max_output_tokens");
    }

    [Fact]
    public void OversizedNormalizedPayloadIsCountedWithoutPayloadSizedCopy()
    {
        string payload = string.Concat(
            "{\"input\":\"",
            new string('x', 4 * 1024 * 1024),
            "\"}");
        using JsonDocument document = JsonDocument.Parse(payload);
        ConservativeTokenEstimator estimator = new(new GatewayEstimationOptions());

        using JsonDocument warmupDocument = JsonDocument.Parse("{\"input\":\"warmup\"}");
        _ = estimator.Estimate(
            InboundProtocol.Responses,
            warmupDocument.RootElement);
        long before = GC.GetAllocatedBytesForCurrentThread();

        Result<GatewayTokenEstimate> result = estimator.Estimate(
            InboundProtocol.Responses,
            document.RootElement);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertValidationPointer(result, "/input");
        Assert.InRange(allocated, 0, 1_000_000);
    }

    [Fact]
    public void NormalizedUsageIsLosslessAndRejectsInconsistentOrUnboundedEvidence()
    {
        BigInteger huge = BigInteger.Parse(
            "999999999999999999999999999999999999999999999999999999999999999999999999999999",
            CultureInfo.InvariantCulture);
        using JsonDocument evidence = JsonDocument.Parse("{\"input_tokens\":1}");
        NormalizedUpstreamUsage usage = new(
            huge,
            BigInteger.One,
            BigInteger.Zero,
            BigInteger.Zero,
            BigInteger.Zero,
            evidence.RootElement);

        Assert.Equal(huge, usage.InputTokens);
        Assert.False(usage.IsOpenAiSafeIntegerShape);
        Assert.Equal(JsonValueKind.Object, usage.RawEvidence?.ValueKind);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NormalizedUpstreamUsage(
                1,
                0,
                cacheReadTokens: 2,
                cacheCreationTokens: 0,
                thinkingTokens: 0,
                rawEvidence: null));

        using JsonDocument oversized = JsonDocument.Parse(
            $"{{\"raw\":\"{new string('x', 65_537)}\"}}");
        Assert.Throws<ArgumentException>(() =>
            new NormalizedUpstreamUsage(1, 0, 0, 0, 0, oversized.RootElement));
    }

    private static void AssertValidationPointer(
        Result<GatewayTokenEstimate> result,
        string expectedPointer)
    {
        Assert.True(result.IsFailure);
        Assert.Equal("validation_failed", result.Error.Code);
        Assert.NotNull(result.Error.Presentation?.Errors);
        Assert.Contains(expectedPointer, result.Error.Presentation!.Errors!.Keys);
    }
}
