using PoolAI.BuildingBlocks;

namespace PoolAI.UnitTests;

public sealed class ResultTests
{
    [Fact]
    public void FailedResultPreservesStableErrorAndHidesValue()
    {
        Result<int> result = Result.Failure<int>("example_error", "Example failure.");

        Assert.True(result.IsFailure);
        Assert.Equal("example_error", result.Error.Code);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void EntityIdentifierRejectsEmptyGuid()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => new EntityId(Guid.Empty));

        Assert.Equal("value", exception.ParamName);
    }
}
