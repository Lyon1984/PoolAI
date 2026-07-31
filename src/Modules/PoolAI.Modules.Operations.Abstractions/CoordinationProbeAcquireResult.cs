using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationProbeAcquireResult(
    CoordinationProbeAcquireDisposition Disposition,
    DateTimeOffset ProbeExpiresAt,
    TimeSpan RetryAfter)
{
    public static CoordinationProbeAcquireResult Acquired(DateTimeOffset probeExpiresAt) =>
        new(CoordinationProbeAcquireDisposition.Acquired, probeExpiresAt, TimeSpan.Zero);

    public static CoordinationProbeAcquireResult Rejected(TimeSpan retryAfter) =>
        new(CoordinationProbeAcquireDisposition.Rejected, default, retryAfter);

    public static CoordinationProbeAcquireResult Unavailable { get; } =
        new(CoordinationProbeAcquireDisposition.Unavailable, default, TimeSpan.Zero);
}
