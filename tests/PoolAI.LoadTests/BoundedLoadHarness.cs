namespace PoolAI.LoadTests;

internal sealed class BoundedLoadHarness
{
    public static async ValueTask<LoadRunResult> RunAsync(
        int requestCount,
        int maxConcurrency,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentNullException.ThrowIfNull(operation);

        using SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);
        int active = 0;
        int completed = 0;
        int peak = 0;

        Task[] operations = Enumerable.Range(0, requestCount)
            .Select(_ => ExecuteOneAsync())
            .ToArray();
        await Task.WhenAll(operations).ConfigureAwait(false);
        return new LoadRunResult(requestCount, completed, peak);

        async Task ExecuteOneAsync()
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            int current = Interlocked.Increment(ref active);
            UpdatePeak(ref peak, current);
            try
            {
                await operation(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
            }
            finally
            {
                Interlocked.Decrement(ref active);
                gate.Release();
            }
        }
    }

    private static void UpdatePeak(ref int peak, int candidate)
    {
        int observed = Volatile.Read(ref peak);
        while (candidate > observed)
        {
            int prior = Interlocked.CompareExchange(ref peak, candidate, observed);
            if (prior == observed)
            {
                return;
            }

            observed = prior;
        }
    }
}
