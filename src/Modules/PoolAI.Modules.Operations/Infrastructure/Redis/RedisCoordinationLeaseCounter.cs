using PoolAI.Modules.Operations.Abstractions;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Redis;

#pragma warning disable MA0051 // Validation and fail-closed Redis counting stay visible.
internal sealed class RedisCoordinationLeaseCounter(
    RedisConnectionProvider connections,
    RedisScriptEvaluator evaluator,
    RuntimeDependencyOptions options) : ICoordinationLeaseCounter
{
    private const int MaximumBatchSize = 100;
    private const int MaximumActiveLeases = 10_000;
    private readonly RedisConnectionProvider _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));
    private readonly RedisScriptEvaluator _evaluator =
        evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    private readonly RuntimeDependencyOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<CoordinationLeaseCountResult> CountActiveAsync(
        IReadOnlyList<string> keyBases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keyBases);
        if (keyBases.Count is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(keyBases));
        }

        HashSet<string> distinctKeys = new(StringComparer.Ordinal);
        foreach (string keyBase in keyBases)
        {
            RedisCoordinationKeyGuard.ValidateAccountLease(keyBase);
            if (!distinctKeys.Add(keyBase))
            {
                throw new ArgumentException(
                    "Account lease keys must be unique.",
                    nameof(keyBases));
            }
        }

        try
        {
            DateTimeOffset sampledAt = await _evaluator
                .GetServerTimeAsync(cancellationToken)
                .ConfigureAwait(false);
            ConnectionMultiplexer connection = await _connections
                .GetAsync(cancellationToken)
                .ConfigureAwait(false);
            IDatabase database = connection.GetDatabase();
            double sampledAtMilliseconds = sampledAt.ToUnixTimeMilliseconds();
            Task<long>[] pendingCounts = keyBases
                .Select(keyBase => database.SortedSetLengthAsync(
                    FullKey(keyBase),
                    sampledAtMilliseconds,
                    double.PositiveInfinity,
                    Exclude.Start))
                .ToArray();
            long[] observedCounts = await Task.WhenAll(pendingCounts)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            int[] counts = new int[observedCounts.Length];
            for (int index = 0; index < observedCounts.Length; index++)
            {
                long observed = observedCounts[index];
                if (observed is < 0 or > MaximumActiveLeases)
                {
                    return CoordinationLeaseCountResult.Unavailable;
                }

                counts[index] = checked((int)observed);
            }

            return CoordinationLeaseCountResult.Counted(counts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return CoordinationLeaseCountResult.Unavailable;
        }
    }

    private RedisKey FullKey(string keyBase) => $"{_options.RedisKeyPrefix}{keyBase}";

    private static bool IsUnavailable(Exception exception) =>
        exception is RedisException
            or TimeoutException
            or InvalidOperationException
            or OverflowException
            or ArgumentOutOfRangeException;
}
#pragma warning restore MA0051
