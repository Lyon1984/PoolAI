namespace PoolAI.BuildingBlocks;

public sealed record ResultErrorPresentation(
    string Code,
    int Status,
    string Title,
    string Detail,
    bool Retryable,
    long? RetryAfterSeconds = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);
