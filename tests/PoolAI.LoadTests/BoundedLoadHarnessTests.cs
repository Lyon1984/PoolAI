namespace PoolAI.LoadTests;

public sealed class BoundedLoadHarnessTests
{
    [Fact]
    public async Task HarnessNeverExceedsTheConfiguredConcurrency()
    {
        const int MaxConcurrency = 4;
        TaskCompletionSource saturated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;

        ValueTask Operation(CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref active);
            if (current == MaxConcurrency)
            {
                saturated.TrySetResult();
            }

            return AwaitReleaseAsync(cancellationToken);
        }

        async ValueTask AwaitReleaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        ValueTask<LoadRunResult> run = BoundedLoadHarness.RunAsync(
            32,
            MaxConcurrency,
            Operation,
            TestContext.Current.CancellationToken);
        await saturated.Task.WaitAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        LoadRunResult result = await run;

        Assert.Equal(32, result.Scheduled);
        Assert.Equal(32, result.Completed);
        Assert.Equal(MaxConcurrency, result.PeakConcurrency);
    }
}
