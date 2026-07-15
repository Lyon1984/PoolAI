namespace PoolAI.Modules.Operations.Abstractions;

public readonly record struct WorkerJobIdentity
{
    public WorkerJobIdentity(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}
