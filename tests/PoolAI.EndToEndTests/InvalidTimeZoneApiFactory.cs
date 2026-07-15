using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace PoolAI.EndToEndTests;

internal sealed class InvalidTimeZoneApiFactory : PoolAiApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["App:TimeZone"] = "Invalid/PoolAI-Time-Zone",
            }));
    }
}
