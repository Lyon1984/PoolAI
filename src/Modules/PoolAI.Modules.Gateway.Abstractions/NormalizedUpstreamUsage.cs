using System.Numerics;
using System.Text;

namespace PoolAI.Modules.Gateway.Abstractions;

/// <summary>
/// Lossless, provider-neutral Token usage captured before any public
/// safe-integer projection is attempted.
/// </summary>
public sealed record NormalizedUpstreamUsage
{
    private const int MaximumRawEvidenceUtf8Bytes = 65_536;
    private static readonly BigInteger MaximumOpenAiSafeInteger =
        new(9_007_199_254_740_991L);

    public NormalizedUpstreamUsage(
        BigInteger inputTokens,
        BigInteger outputTokens,
        BigInteger cacheReadTokens,
        BigInteger cacheCreationTokens,
        BigInteger thinkingTokens,
        JsonElement? rawEvidence)
    {
        if (inputTokens < BigInteger.Zero
            || outputTokens < BigInteger.Zero
            || cacheReadTokens < BigInteger.Zero
            || cacheCreationTokens < BigInteger.Zero
            || thinkingTokens < BigInteger.Zero
            || cacheReadTokens > inputTokens
            || cacheCreationTokens > inputTokens
            || cacheReadTokens + cacheCreationTokens > inputTokens
            || thinkingTokens > outputTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputTokens),
                "The normalized upstream usage components are inconsistent.");
        }

        if (rawEvidence is JsonElement evidence)
        {
            if (evidence.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(evidence.GetRawText())
                    > MaximumRawEvidenceUtf8Bytes)
            {
                throw new ArgumentException(
                    "The normalized upstream usage evidence is invalid.",
                    nameof(rawEvidence));
            }

            RawEvidence = evidence.Clone();
        }

        InputTokens = inputTokens;
        OutputTokens = outputTokens;
        CacheReadTokens = cacheReadTokens;
        CacheCreationTokens = cacheCreationTokens;
        ThinkingTokens = thinkingTokens;
    }

    public BigInteger InputTokens { get; }

    public BigInteger OutputTokens { get; }

    public BigInteger CacheReadTokens { get; }

    public BigInteger CacheCreationTokens { get; }

    public BigInteger ThinkingTokens { get; }

    public JsonElement? RawEvidence { get; }

    public BigInteger TotalTokens => InputTokens + OutputTokens;

    public bool IsOpenAiSafeIntegerShape =>
        InputTokens <= MaximumOpenAiSafeInteger
        && OutputTokens <= MaximumOpenAiSafeInteger
        && CacheReadTokens <= MaximumOpenAiSafeInteger
        && CacheCreationTokens <= MaximumOpenAiSafeInteger
        && ThinkingTokens <= MaximumOpenAiSafeInteger
        && TotalTokens <= MaximumOpenAiSafeInteger;

    public override string ToString() => nameof(NormalizedUpstreamUsage);
}
