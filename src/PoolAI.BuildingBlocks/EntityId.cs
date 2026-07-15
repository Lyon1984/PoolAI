namespace PoolAI.BuildingBlocks;

public readonly record struct EntityId
{
    public EntityId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An entity identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static EntityId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString("D");
}
