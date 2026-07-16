using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace PoolAI.EndToEndTests;

internal sealed class InvalidJwtSigningKeyApiFactory : PoolAiApiFactory
{
    internal const string InvalidValue = "not-base64-sensitive-material";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Auth:Jwt:SigningKey", InvalidValue);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["Auth:Jwt:SigningKey"] = InvalidValue,
                }));
    }
}
