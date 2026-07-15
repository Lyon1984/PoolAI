namespace PoolAI.BuildingBlocks;

public sealed record ResultError(string Code, string Description)
{
    public static ResultError None { get; } = new(string.Empty, string.Empty);
}
