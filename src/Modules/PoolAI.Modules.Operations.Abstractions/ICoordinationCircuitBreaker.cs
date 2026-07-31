namespace PoolAI.Modules.Operations.Abstractions;

public interface ICoordinationCircuitBreaker
{
    ValueTask<CoordinationBreakerRecordResult> RecordAsync(
        CoordinationBreakerRecordRequest request,
        CancellationToken cancellationToken);

    ValueTask<CoordinationProbeAcquireResult> AcquireProbeAsync(
        CoordinationProbeAcquireRequest request,
        CancellationToken cancellationToken);

    ValueTask<CoordinationProbeCompleteResult> CompleteProbeAsync(
        CoordinationProbeCompleteRequest request,
        CancellationToken cancellationToken);
}
