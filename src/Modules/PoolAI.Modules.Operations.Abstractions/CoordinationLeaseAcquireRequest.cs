namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CoordinationLeaseAcquireRequest(
    string KeyBase,
    string Owner,
    int Limit)
{
    public override string ToString() => nameof(CoordinationLeaseAcquireRequest);
}
