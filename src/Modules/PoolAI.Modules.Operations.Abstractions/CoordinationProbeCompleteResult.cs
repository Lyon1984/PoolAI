using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationProbeCompleteResult(
    CoordinationProbeCompleteDisposition Disposition,
    CoordinationBreakerState State,
    CoordinationBreakerAction Action,
    long HalfOpenSuccesses,
    DateTimeOffset OpenUntil)
{
    public static CoordinationProbeCompleteResult Completed(
        CoordinationBreakerState state,
        CoordinationBreakerAction action,
        long halfOpenSuccesses,
        DateTimeOffset openUntil) =>
        new(
            CoordinationProbeCompleteDisposition.Completed,
            state,
            action,
            halfOpenSuccesses,
            openUntil);

    public static CoordinationProbeCompleteResult NotOwner { get; } =
        new(
            CoordinationProbeCompleteDisposition.NotOwner,
            CoordinationBreakerState.Closed,
            CoordinationBreakerAction.None,
            0,
            default);

    public static CoordinationProbeCompleteResult Unavailable { get; } =
        new(
            CoordinationProbeCompleteDisposition.Unavailable,
            CoordinationBreakerState.Unavailable,
            CoordinationBreakerAction.None,
            0,
            default);
}
