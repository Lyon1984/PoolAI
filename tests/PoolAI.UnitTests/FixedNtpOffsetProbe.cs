using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

internal sealed class FixedNtpOffsetProbe(TimeSpan offset) : INtpOffsetProbe
{
    internal int CallCount { get; private set; }

    public ValueTask<NtpOffsetProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return ValueTask.FromResult(NtpOffsetProbeResult.Available(offset));
    }
}
