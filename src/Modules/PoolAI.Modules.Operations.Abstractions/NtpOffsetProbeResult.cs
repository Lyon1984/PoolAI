namespace PoolAI.Modules.Operations.Abstractions;

public readonly record struct NtpOffsetProbeResult(bool IsAvailable, TimeSpan Offset)
{
    public static NtpOffsetProbeResult Available(TimeSpan offset) => new(true, offset);

    public static NtpOffsetProbeResult Unavailable { get; } = new(false, TimeSpan.Zero);
}
