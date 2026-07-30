using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationLeaseRenewResult(
    CoordinationLeaseRenewDisposition Disposition,
    DateTimeOffset ExpiresAt)
{
    public static CoordinationLeaseRenewResult Renewed(DateTimeOffset expiresAt) =>
        new(CoordinationLeaseRenewDisposition.Renewed, expiresAt);

    public static CoordinationLeaseRenewResult Lost { get; } =
        new(CoordinationLeaseRenewDisposition.Lost, default);

    public static CoordinationLeaseRenewResult Unavailable { get; } =
        new(CoordinationLeaseRenewDisposition.Unavailable, default);
}
