using System.Text.Json;

namespace PoolAI.UnitTests;

public sealed class FailSkipsGateProbe
{
    [Fact]
    public void DynamicSkipMustFail()
    {
        if (string.Equals(
            Environment.GetEnvironmentVariable("POOLAI_FAIL_SKIPS_GATE_PROBE"),
            "1",
            StringComparison.Ordinal))
        {
            Assert.Skip("Intentional quality-gate probe: a skipped test must fail the build.");
        }

        string configurationPath = Path.Combine(AppContext.BaseDirectory, "xunit.runner.json");
        using JsonDocument configuration = JsonDocument.Parse(File.ReadAllText(configurationPath));

        Assert.True(configuration.RootElement.GetProperty("failSkips").GetBoolean());
    }
}
