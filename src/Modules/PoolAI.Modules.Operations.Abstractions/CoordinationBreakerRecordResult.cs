using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationBreakerRecordResult(
    CoordinationBreakerRecordDisposition Disposition,
    CoordinationBreakerState State,
    CoordinationBreakerAction Action,
    long Samples,
    long Failures,
    long ConsecutiveFailures,
    DateTimeOffset OpenUntil)
{
    public static CoordinationBreakerRecordResult Recorded(
        CoordinationBreakerState state,
        CoordinationBreakerAction action,
        long samples,
        long failures,
        long consecutiveFailures,
        DateTimeOffset openUntil) =>
        new(
            CoordinationBreakerRecordDisposition.Recorded,
            state,
            action,
            samples,
            failures,
            consecutiveFailures,
            openUntil);

    public static CoordinationBreakerRecordResult Unavailable { get; } =
        new(
            CoordinationBreakerRecordDisposition.Unavailable,
            CoordinationBreakerState.Unavailable,
            CoordinationBreakerAction.None,
            0,
            0,
            0,
            default);
}
