using System.Globalization;
using System.Numerics;
using PoolAI.Modules.GroupQuota.Abstractions;

namespace PoolAI.Modules.Usage.Application;

internal static class UsageHourlyProjectionCalculator
{
    private static readonly BigInteger MaximumNumeric78 = BigInteger.Parse(
        new string('9', 78),
        CultureInfo.InvariantCulture);

    internal static UsageHourProjection? TryCreate(
        AttemptSettlementHourSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!TryAggregate(snapshot.Facts, out UsageHourlyAggregate group))
        {
            return null;
        }

        List<AccountUsageHourProjection> accounts = [];
        foreach (IGrouping<EntityId, AttemptSettlementFact> accountFacts in snapshot.Facts
            .GroupBy(static fact => fact.AccountId)
            .OrderBy(
                static facts => facts.Key.Value.ToString("N"),
                StringComparer.Ordinal))
        {
            if (!TryAggregate(accountFacts, out UsageHourlyAggregate account))
            {
                return null;
            }

            accounts.Add(new AccountUsageHourProjection(accountFacts.Key, account));
        }

        return new UsageHourProjection(
            snapshot.GroupId,
            snapshot.PeriodId,
            snapshot.BucketStart,
            group,
            accounts);
    }

    private static bool TryAggregate(
        IEnumerable<AttemptSettlementFact> source,
        out UsageHourlyAggregate aggregate)
    {
        AttemptSettlementFact[] facts = source.ToArray();
        BigInteger input = BigInteger.Zero;
        BigInteger output = BigInteger.Zero;
        BigInteger cacheCreation = BigInteger.Zero;
        BigInteger cacheRead = BigInteger.Zero;
        BigInteger thinking = BigInteger.Zero;
        foreach (AttemptSettlementFact fact in facts)
        {
            TokenUsage effective = fact.Adjustment?.CorrectedTokens ?? fact.Usage.Tokens;
            if (!IsValid(fact, effective)
                || !TryAdd(ref input, effective.InputTokens)
                || !TryAdd(ref output, effective.OutputTokens)
                || !TryAdd(ref cacheCreation, effective.CacheCreationTokens)
                || !TryAdd(ref cacheRead, effective.CacheReadTokens)
                || !TryAdd(ref thinking, effective.ThinkingTokens))
            {
                aggregate = null!;
                return false;
            }
        }

        if (input + output > MaximumNumeric78)
        {
            aggregate = null!;
            return false;
        }

        aggregate = new UsageHourlyAggregate(
            facts.Select(static fact => fact.RequestId).Distinct().LongCount(),
            facts.LongLength,
            facts.LongCount(static fact =>
                fact.Outcome is UsageAttemptOutcome.Failed or UsageAttemptOutcome.Cancelled),
            facts.LongCount(static fact => fact.AttemptIndex > 0),
            facts.LongCount(static fact => fact.Usage.IsEstimated),
            input,
            output,
            cacheCreation,
            cacheRead,
            thinking);
        return true;
    }

    private static bool IsValid(AttemptSettlementFact fact, TokenUsage effective)
    {
        AttemptUsageAdjustment? adjustment = fact.Adjustment;
        return fact.AttemptIndex >= 0
            && fact.CompletedAt >= fact.DispatchStartedAt
            && IsValid(effective)
            && (adjustment is null
                || adjustment.PreviousTotalTokens == fact.Usage.Tokens.TotalTokens
                && adjustment.DeltaTokens
                    == adjustment.CorrectedTokens.TotalTokens
                        - adjustment.PreviousTotalTokens
                && adjustment.AdjustedAt >= fact.CompletedAt);
    }

    private static bool IsValid(TokenUsage usage) =>
        usage.InputTokens >= BigInteger.Zero
        && usage.OutputTokens >= BigInteger.Zero
        && usage.CacheReadTokens >= BigInteger.Zero
        && usage.CacheCreationTokens >= BigInteger.Zero
        && usage.ThinkingTokens >= BigInteger.Zero
        && usage.CacheReadTokens + usage.CacheCreationTokens <= usage.InputTokens
        && usage.ThinkingTokens <= usage.OutputTokens
        && usage.InputTokens <= MaximumNumeric78
        && usage.OutputTokens <= MaximumNumeric78;

    private static bool TryAdd(ref BigInteger total, BigInteger value)
    {
        total += value;
        return total <= MaximumNumeric78;
    }
}
