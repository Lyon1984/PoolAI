namespace PoolAI.BuildingBlocks;

public static class Result
{
    public static Result<T> Success<T>(T value) => new(true, value, ResultError.None);

    public static Result<T> Failure<T>(
        string code,
        string description,
        long? retryAfterSeconds = null,
        string? etag = null,
        ResultErrorPresentation? presentation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (retryAfterSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds));
        }

        if (etag is not null && string.IsNullOrWhiteSpace(etag))
        {
            throw new ArgumentException("The ETag cannot be blank.", nameof(etag));
        }

        if (presentation is not null
            && (!string.Equals(presentation.Code, code, StringComparison.Ordinal)
                || presentation.Status is < 400 or > 599
                || string.IsNullOrWhiteSpace(presentation.Title)
                || string.IsNullOrWhiteSpace(presentation.Detail)
                || presentation.RetryAfterSeconds != retryAfterSeconds
                || presentation.Errors is not null && presentation.Errors.Count == 0))
        {
            throw new ArgumentException(
                "The error presentation is incomplete or invalid.",
                nameof(presentation));
        }

        return new Result<T>(
            false,
            default,
            new ResultError(code, description, retryAfterSeconds, etag, presentation));
    }
}

public sealed class Result<T>
{
    private readonly T? value;

    internal Result(bool isSuccess, T? value, ResultError error)
    {
        IsSuccess = isSuccess;
        this.value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public ResultError Error { get; }

    public T Value => IsSuccess
        ? value!
        : throw new InvalidOperationException("A failed result has no value.");

}
