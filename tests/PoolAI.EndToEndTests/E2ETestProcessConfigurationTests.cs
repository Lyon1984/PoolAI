namespace PoolAI.EndToEndTests;

public sealed class E2ETestProcessConfigurationTests
{
    [Fact]
    public void TestHostDisablesConfigurationFileReload()
    {
        Assert.Equal(
            "false",
            Environment.GetEnvironmentVariable(
                "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE"));
    }
}
