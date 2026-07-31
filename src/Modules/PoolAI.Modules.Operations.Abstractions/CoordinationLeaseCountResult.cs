using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationLeaseCountResult(
    CoordinationLeaseCountDisposition Disposition,
    IReadOnlyList<int> ActiveCounts)
{
    public static CoordinationLeaseCountResult Counted(
        IReadOnlyList<int> activeCounts)
    {
        ArgumentNullException.ThrowIfNull(activeCounts);
        return new(
            CoordinationLeaseCountDisposition.Counted,
            activeCounts.ToArray());
    }

    public static CoordinationLeaseCountResult Unavailable { get; } =
        new(CoordinationLeaseCountDisposition.Unavailable, Array.Empty<int>());
}
