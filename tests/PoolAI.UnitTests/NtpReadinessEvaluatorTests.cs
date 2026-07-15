using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.UnitTests;

public sealed class NtpReadinessEvaluatorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(5_000, true)]
    [InlineData(-5_000, true)]
    [InlineData(5_001, false)]
    [InlineData(-5_001, false)]
    public async Task ReadinessBoundaryIsExactlyFiveSeconds(
        int offsetMilliseconds,
        bool expected)
    {
        FixedNtpOffsetProbe probe = new(TimeSpan.FromMilliseconds(offsetMilliseconds));

        bool actual = await NtpReadinessEvaluator.IsReadyAsync(
            probe,
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
        Assert.Equal(1, probe.CallCount);
    }

}
