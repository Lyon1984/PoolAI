using System.Runtime.InteropServices;

namespace PoolAI.Modules.Operations.Abstractions;

[StructLayout(LayoutKind.Auto)]
public readonly record struct CoordinationValueReadResult(
    CoordinationValueReadDisposition Disposition,
    string? Value)
{
    public static CoordinationValueReadResult Found(string value) =>
        new(CoordinationValueReadDisposition.Found, value);

    public static CoordinationValueReadResult Missing { get; } =
        new(CoordinationValueReadDisposition.Missing, null);

    public static CoordinationValueReadResult Unavailable { get; } =
        new(CoordinationValueReadDisposition.Unavailable, null);
}
