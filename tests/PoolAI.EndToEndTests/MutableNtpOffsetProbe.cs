using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.EndToEndTests;

internal sealed class MutableNtpOffsetProbe : INtpOffsetProbe
{
    private readonly Lock gate = new();
    private NtpOffsetProbeResult result = NtpOffsetProbeResult.Available(TimeSpan.Zero);

    internal void SetAvailable(TimeSpan offset)
    {
        lock (gate)
        {
            result = NtpOffsetProbeResult.Available(offset);
        }
    }

    internal void SetUnavailable()
    {
        lock (gate)
        {
            result = NtpOffsetProbeResult.Unavailable;
        }
    }

    public ValueTask<NtpOffsetProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return ValueTask.FromResult(result);
        }
    }
}
