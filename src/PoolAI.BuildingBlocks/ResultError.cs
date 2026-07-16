namespace PoolAI.BuildingBlocks;

public sealed record ResultError(
    string Code,
    string Description,
    long? RetryAfterSeconds = null,
    string? ETag = null,
    ResultErrorPresentation? Presentation = null)
{
    public static ResultError None { get; } = new(string.Empty, string.Empty);
}
