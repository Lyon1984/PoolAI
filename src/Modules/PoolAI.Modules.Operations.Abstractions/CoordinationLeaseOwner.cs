namespace PoolAI.Modules.Operations.Abstractions;

public sealed record CoordinationLeaseOwner(string KeyBase, string Owner)
{
    public override string ToString() => nameof(CoordinationLeaseOwner);
}
