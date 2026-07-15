namespace PoolAI.Modules.Operations.Abstractions;

public interface INtpOffsetProbe
{
    ValueTask<NtpOffsetProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
