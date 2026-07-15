namespace PoolAI.Modules.Operations.Abstractions;

public static class NtpReadinessEvaluator
{
    private static readonly TimeSpan MaximumOffset = TimeSpan.FromSeconds(5);

    public static bool IsReady(TimeSpan offset) =>
        offset >= -MaximumOffset && offset <= MaximumOffset;

    public static async ValueTask<bool> IsReadyAsync(
        INtpOffsetProbe probe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        NtpOffsetProbeResult result = await probe
            .ProbeAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.IsAvailable && IsReady(result.Offset);
    }
}
