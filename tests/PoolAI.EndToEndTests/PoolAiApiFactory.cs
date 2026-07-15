using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.EndToEndTests;

internal class PoolAiApiFactory : WebApplicationFactory<Program>
{
    internal MutableNtpOffsetProbe NtpOffsetProbe { get; } = new();

    internal ReadyRuntimeDependencyReadiness DependencyReadiness { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Dictionary<string, string?> configurationValues = ValidConfiguration();
        builder.UseEnvironment("Development");
        // Minimal-hosting service registration reads this value before the
        // ConfigureAppConfiguration callback is applied by WebApplicationFactory.
        builder.UseSetting(
            "Data:Postgres:ConnectionString",
            configurationValues["Data:Postgres:ConnectionString"]);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(configurationValues));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRuntimeDependencyReadiness>();
            services.AddSingleton<IRuntimeDependencyReadiness>(DependencyReadiness);
            services.RemoveAll<INtpOffsetProbe>();
            services.AddSingleton<INtpOffsetProbe>(NtpOffsetProbe);
        });
    }

    private static Dictionary<string, string?> ValidConfiguration()
    {
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["App:PublicBaseUrl"] = "http://localhost",
            ["App:TimeZone"] = "Asia/Shanghai",
            ["App:AllowedHosts:0"] = "localhost",
            ["Cors:AllowedOrigins:0"] = "http://localhost",
            ["Auth:Jwt:SigningKey"] = secret,
            ["ApiKeys:CurrentPepper"] = secret,
            ["Data:Postgres:ConnectionString"] =
                $"Host=localhost;Database=poolai;Username=poolai;Password={password}",
            ["Data:Redis:ConnectionString"] =
                $"localhost:6379,user=poolai,password={password},abortConnect=false",
            ["Data:Redis:KeyPrefix"] = "poolai:r1:test:",
            ["Email:Smtp:Host"] = "localhost",
            ["Email:Smtp:Security"] = "starttls",
            ["Email:FromAddress"] = "noreply@localhost.test",
            ["Secrets:Envelope:CurrentKeyId"] = "test-kek-v1",
            ["Secrets:Envelope:CurrentKey"] = secret,
            ["Secrets:Envelope:DecryptKeyRing:test-kek-v1"] = secret,
            ["Health:Ntp:Server"] = "127.0.0.1",
        };
    }
}
