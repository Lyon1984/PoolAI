using PoolAI.Modules.Operations.Abstractions;
using PoolAI.Modules.Operations.Infrastructure.Redis;
using StackExchange.Redis;

namespace PoolAI.UnitTests;

// Governing contract: docs/runtime/redis-contract.md, section 7.3.
public sealed class RedisCircuitBreakerAdapterContractTests
{
    private static readonly DateTimeOffset Deadline =
        DateTimeOffset.FromUnixTimeMilliseconds(1_900_000_000_000);

    [Theory]
    [MemberData(nameof(ValidRecordTuples))]
    public void RecordParserAcceptsOnlyContractStateActionTuples(
        long[] tuple,
        CoordinationBreakerState expectedState,
        CoordinationBreakerAction expectedAction)
    {
        CoordinationBreakerRecordResult result =
            RedisCoordinationCircuitBreaker.ParseRecord(Integers(tuple));

        Assert.Equal(
            CoordinationBreakerRecordDisposition.Recorded,
            result.Disposition);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(tuple[1], result.Samples);
        Assert.Equal(tuple[2], result.Failures);
        Assert.Equal(tuple[3], result.ConsecutiveFailures);
        Assert.Equal(
            tuple[4] == 0
                ? default
                : DateTimeOffset.FromUnixTimeMilliseconds(tuple[4]),
            result.OpenUntil);
    }

    [Theory]
    [MemberData(nameof(InvalidRecordTuples))]
    public void RecordParserFailsClosedForCorruptOrUnknownTuples(
        RedisResult result)
    {
        CoordinationBreakerRecordResult parsed =
            RedisCoordinationCircuitBreaker.ParseRecord(result);

        Assert.Equal(CoordinationBreakerRecordResult.Unavailable, parsed);
    }

    [Fact]
    public void RecordParserRejectsWrongLengthAndRespTypes()
    {
        Assert.Equal(
            CoordinationBreakerRecordResult.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(
                Integers(0, 0, 0, 0, 0)));
        Assert.Equal(
            CoordinationBreakerRecordResult.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(
                BulkStrings("0", "0", "0", "0", "0", "0")));
        Assert.Equal(
            CoordinationBreakerRecordResult.Unavailable,
            RedisCoordinationCircuitBreaker.ParseRecord(null!));
    }

    [Theory]
    [InlineData(1, 1_900_000_010_000, CoordinationProbeAcquireDisposition.Acquired)]
    [InlineData(0, 0, CoordinationProbeAcquireDisposition.Rejected)]
    [InlineData(0, 2_500, CoordinationProbeAcquireDisposition.Rejected)]
    public void ProbeAcquireParserMapsTheCompleteAbi(
        long code,
        long value,
        CoordinationProbeAcquireDisposition expected)
    {
        CoordinationProbeAcquireResult result =
            RedisCoordinationCircuitBreaker.ParseProbeAcquire(
                Integers(code, value));

        Assert.Equal(expected, result.Disposition);
        if (expected == CoordinationProbeAcquireDisposition.Acquired)
        {
            Assert.Equal(
                DateTimeOffset.FromUnixTimeMilliseconds(value),
                result.ProbeExpiresAt);
            Assert.Equal(TimeSpan.Zero, result.RetryAfter);
        }
        else
        {
            Assert.Equal(default, result.ProbeExpiresAt);
            Assert.Equal(TimeSpan.FromMilliseconds(value), result.RetryAfter);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidAcquireTuples))]
    public void ProbeAcquireParserFailsClosedForInvalidResults(
        RedisResult result)
    {
        Assert.Equal(
            CoordinationProbeAcquireResult.Unavailable,
            RedisCoordinationCircuitBreaker.ParseProbeAcquire(result));
    }

    [Theory]
    [MemberData(nameof(ValidCompleteTuples))]
    public void ProbeCompleteParserMapsEveryContractResult(
        long[] tuple,
        CoordinationProbeCompleteDisposition expectedDisposition,
        CoordinationBreakerState expectedState,
        CoordinationBreakerAction expectedAction)
    {
        CoordinationProbeCompleteResult result =
            RedisCoordinationCircuitBreaker.ParseProbeComplete(Integers(tuple));

        Assert.Equal(expectedDisposition, result.Disposition);
        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(tuple[2], result.HalfOpenSuccesses);
        Assert.Equal(
            tuple[3] == 0
                ? default
                : DateTimeOffset.FromUnixTimeMilliseconds(tuple[3]),
            result.OpenUntil);
    }

    [Theory]
    [MemberData(nameof(InvalidCompleteTuples))]
    public void ProbeCompleteParserFailsClosedForCorruptOrUnknownTuples(
        RedisResult result)
    {
        Assert.Equal(
            CoordinationProbeCompleteResult.Unavailable,
            RedisCoordinationCircuitBreaker.ParseProbeComplete(result));
    }

    [Fact]
    public void CoordinationResultFactoriesPreserveOnlyTheirContractFields()
    {
        CoordinationBreakerRecordResult record =
            CoordinationBreakerRecordResult.Recorded(
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling,
                samples: 10,
                failures: 6,
                consecutiveFailures: 5,
                Deadline);
        CoordinationProbeAcquireResult acquired =
            CoordinationProbeAcquireResult.Acquired(Deadline);
        CoordinationProbeAcquireResult rejected =
            CoordinationProbeAcquireResult.Rejected(TimeSpan.FromSeconds(3));
        CoordinationProbeCompleteResult completed =
            CoordinationProbeCompleteResult.Completed(
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown,
                halfOpenSuccesses: 1,
                openUntil: default);

        Assert.Equal(
            CoordinationBreakerRecordDisposition.Recorded,
            record.Disposition);
        Assert.Equal(CoordinationBreakerState.Open, record.State);
        Assert.Equal(CoordinationBreakerAction.WriteCooling, record.Action);
        Assert.Equal(10, record.Samples);
        Assert.Equal(6, record.Failures);
        Assert.Equal(5, record.ConsecutiveFailures);
        Assert.Equal(Deadline, record.OpenUntil);

        Assert.Equal(
            CoordinationProbeAcquireDisposition.Acquired,
            acquired.Disposition);
        Assert.Equal(Deadline, acquired.ProbeExpiresAt);
        Assert.Equal(TimeSpan.Zero, acquired.RetryAfter);
        Assert.Equal(
            CoordinationProbeAcquireDisposition.Rejected,
            rejected.Disposition);
        Assert.Equal(default, rejected.ProbeExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(3), rejected.RetryAfter);
        Assert.Equal(
            CoordinationProbeAcquireDisposition.Unavailable,
            CoordinationProbeAcquireResult.Unavailable.Disposition);

        Assert.Equal(
            CoordinationProbeCompleteDisposition.Completed,
            completed.Disposition);
        Assert.Equal(CoordinationBreakerState.HalfOpen, completed.State);
        Assert.Equal(CoordinationBreakerAction.WriteUnknown, completed.Action);
        Assert.Equal(1, completed.HalfOpenSuccesses);
        Assert.Equal(
            CoordinationProbeCompleteDisposition.NotOwner,
            CoordinationProbeCompleteResult.NotOwner.Disposition);
        Assert.Equal(
            CoordinationProbeCompleteDisposition.Unavailable,
            CoordinationProbeCompleteResult.Unavailable.Disposition);
    }

    public static TheoryData<
        long[],
        CoordinationBreakerState,
        CoordinationBreakerAction> ValidRecordTuples() =>
        new()
        {
            {
                [0, 0, 0, 0, 0, 0],
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None
            },
            {
                [0, 1, 0, 0, 0, 1],
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy
            },
            {
                [0, 4, 1, 1, 0, 2],
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteDegraded
            },
            {
                [1, 10, 5, 5, 1_900_000_000_000, 0],
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.None
            },
            {
                [1, 10, 5, 5, 1_900_000_000_000, 3],
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling
            },
            {
                [1, 0, 0, 0, 0, 4],
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteUnhealthy
            },
            {
                [2, 0, 0, 0, 0, 0],
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.None
            },
            {
                [2, 0, 0, 0, 0, 5],
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown
            },
            {
                [2, 0, 0, 0, 1, 5],
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown
            },
        };

    public static TheoryData<RedisResult> InvalidRecordTuples() =>
        new()
        {
            Integers(-1, 0, 0, 0, 0, 0),
            Integers(3, 0, 0, 0, 0, 0),
            Integers(0, -1, 0, 0, 0, 0),
            Integers(0, 0, -1, 0, 0, 0),
            Integers(0, 1, 2, 0, 0, 0),
            Integers(0, 0, 0, -1, 0, 0),
            Integers(0, (long)int.MaxValue + 1, 0, 0, 0, 0),
            Integers(0, 0, 0, (long)int.MaxValue + 1, 0, 0),
            Integers(0, 0, 0, 0, -1, 0),
            Integers(0, 0, 0, 0, long.MaxValue, 0),
            Integers(0, 0, 0, 0, 0, 6),
            Integers(0, 0, 0, 1, 0, 1),
            Integers(0, 1, 1, 0, 0, 2),
            Integers(1, 0, 0, 0, 0, 3),
            Integers(1, 0, 0, 0, 1, 4),
            Integers(2, 0, 0, 0, 0, 3),
        };

    public static TheoryData<RedisResult> InvalidAcquireTuples() =>
        new()
        {
            Integers(1),
            Integers(-1, 0),
            Integers(2, 0),
            Integers(1, 0),
            Integers(1, -1),
            Integers(1, long.MaxValue),
            Integers(0, -1),
            BulkStrings("1", "1900000010000"),
            RedisResult.Create((RedisValue)"not-an-array", ResultType.BulkString),
        };

    public static TheoryData<
        long[],
        CoordinationProbeCompleteDisposition,
        CoordinationBreakerState,
        CoordinationBreakerAction> ValidCompleteTuples() =>
        new()
        {
            {
                [0, 0, 0, 0, 0],
                CoordinationProbeCompleteDisposition.NotOwner,
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.None
            },
            {
                [1, 0, 0, 0, 1],
                CoordinationProbeCompleteDisposition.Completed,
                CoordinationBreakerState.Closed,
                CoordinationBreakerAction.WriteHealthy
            },
            {
                [1, 1, 0, 1_900_000_000_000, 3],
                CoordinationProbeCompleteDisposition.Completed,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteCooling
            },
            {
                [1, 1, 0, 0, 4],
                CoordinationProbeCompleteDisposition.Completed,
                CoordinationBreakerState.Open,
                CoordinationBreakerAction.WriteUnhealthy
            },
            {
                [1, 2, 1, 0, 5],
                CoordinationProbeCompleteDisposition.Completed,
                CoordinationBreakerState.HalfOpen,
                CoordinationBreakerAction.WriteUnknown
            },
        };

    public static TheoryData<RedisResult> InvalidCompleteTuples() =>
        new()
        {
            Integers(0, 0, 0, 0),
            Integers(-1, 0, 0, 0, 0),
            Integers(2, 0, 0, 0, 0),
            Integers(0, 1, 0, 0, 0),
            Integers(0, 0, 1, 0, 0),
            Integers(1, 3, 0, 0, 1),
            Integers(1, 0, -1, 0, 1),
            Integers(1, 0, 2, 0, 1),
            Integers(1, 0, 0, -1, 1),
            Integers(1, 0, 0, long.MaxValue, 1),
            Integers(1, 0, 0, 0, 6),
            Integers(1, 0, 1, 0, 1),
            Integers(1, 1, 0, 0, 3),
            Integers(1, 1, 1, 1_900_000_000_000, 3),
            Integers(1, 1, 0, 1_900_000_000_000, 4),
            Integers(1, 2, 0, 0, 5),
            Integers(1, 2, 1, 1_900_000_000_000, 5),
            BulkStrings("1", "0", "0", "0", "1"),
            RedisResult.Create((RedisValue)"not-an-array", ResultType.BulkString),
        };

    private static RedisResult Integers(params long[] values) =>
        RedisResult.Create(
            values
                .Select(static value => RedisResult.Create(
                    (RedisValue)value,
                    ResultType.Integer))
                .ToArray(),
            ResultType.Array);

    private static RedisResult BulkStrings(params string[] values) =>
        RedisResult.Create(
            values
                .Select(static value => RedisResult.Create(
                    (RedisValue)value,
                    ResultType.BulkString))
                .ToArray(),
            ResultType.Array);
}
