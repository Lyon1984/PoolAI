namespace PoolAI.BuildingBlocks;

public static class Result
{
    public static Result<T> Success<T>(T value) => new(true, value, ResultError.None);

    public static Result<T> Failure<T>(string code, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return new Result<T>(false, default, new ResultError(code, description));
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
