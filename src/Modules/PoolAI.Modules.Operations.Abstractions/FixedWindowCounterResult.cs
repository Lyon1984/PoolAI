using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct FixedWindowCounterResult(
    FixedWindowCounterDisposition Disposition,
    long Current,
    TimeSpan RetryAfter)
{
    public static FixedWindowCounterResult Allowed(long current) =>
        new(FixedWindowCounterDisposition.Allowed, current, TimeSpan.Zero);

    public static FixedWindowCounterResult Rejected(long current, TimeSpan retryAfter) =>
        new(FixedWindowCounterDisposition.Rejected, current, retryAfter);

    public static FixedWindowCounterResult Unavailable { get; } =
        new(FixedWindowCounterDisposition.Unavailable, 0, TimeSpan.Zero);
}
