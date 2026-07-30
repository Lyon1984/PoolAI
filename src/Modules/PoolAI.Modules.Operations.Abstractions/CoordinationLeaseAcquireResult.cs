using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationLeaseAcquireResult(
    CoordinationLeaseAcquireDisposition Disposition,
    int ActiveCount,
    DateTimeOffset ExpiresAt,
    TimeSpan RetryAfter)
{
    public static CoordinationLeaseAcquireResult Acquired(
        int activeCount,
        DateTimeOffset expiresAt,
        bool renewed) =>
        new(
            renewed
                ? CoordinationLeaseAcquireDisposition.Renewed
                : CoordinationLeaseAcquireDisposition.Acquired,
            activeCount,
            expiresAt,
            TimeSpan.Zero);

    public static CoordinationLeaseAcquireResult CapacityExceeded(
        int activeCount,
        TimeSpan retryAfter) =>
        new(
            CoordinationLeaseAcquireDisposition.CapacityExceeded,
            activeCount,
            default,
            retryAfter);

    public static CoordinationLeaseAcquireResult Unavailable { get; } =
        new(CoordinationLeaseAcquireDisposition.Unavailable, 0, default, TimeSpan.Zero);
}
