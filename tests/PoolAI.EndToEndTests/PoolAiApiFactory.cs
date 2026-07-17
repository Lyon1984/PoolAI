extern alias PoolAiApi;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoolAI.Modules.Identity.Application;
using PoolAI.Modules.Operations.Abstractions;

namespace PoolAI.EndToEndTests;

internal class PoolAiApiFactory : WebApplicationFactory<PoolAiApi::Program>
{
    internal byte[] JwtSigningKey { get; } = RandomNumberGenerator.GetBytes(32);

    internal MutableNtpOffsetProbe NtpOffsetProbe { get; } = new();

    internal ReadyRuntimeDependencyReadiness DependencyReadiness { get; } = new();

    internal TimeProvider AuthorizationTimeProvider { get; set; } = TimeProvider.System;

    internal RecordingAccessSessionValidator AccessSessionValidator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Dictionary<string, string?> configurationValues = ValidConfiguration();
        builder.UseEnvironment("Development");
        // Minimal-hosting service registration reads these values before the
        // ConfigureAppConfiguration callback is applied by WebApplicationFactory.
        foreach ((string key, string? value) in configurationValues)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(configurationValues));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IRuntimeDependencyReadiness>();
            services.AddSingleton<IRuntimeDependencyReadiness>(DependencyReadiness);
            services.RemoveAll<INtpOffsetProbe>();
            services.AddSingleton<INtpOffsetProbe>(NtpOffsetProbe);
            services.RemoveAll<IAccessSessionValidator>();
            services.AddSingleton<IAccessSessionValidator>(AccessSessionValidator);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(AuthorizationTimeProvider);
        });
    }

    private Dictionary<string, string?> ValidConfiguration()
    {
        string jwtSecret = Convert.ToBase64String(JwtSigningKey);
        string refreshPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string rateLimitPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string tokenPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string recoveryCodePepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string loginRateLimitPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string apiKeyPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string idempotencyPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string envelopeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["App:PublicBaseUrl"] = "http://localhost",
            ["App:TimeZone"] = "Asia/Shanghai",
            ["App:AllowedHosts:0"] = "localhost",
            ["Cors:AllowedOrigins:0"] = "http://localhost",
            ["Auth:Jwt:SigningKey"] = jwtSecret,
            ["Auth:RefreshToken:CurrentPepperVersion"] = "1",
            ["Auth:RefreshToken:CurrentPepper"] = refreshPepper,
            ["Auth:PasswordReset:RateLimitScopePepper"] = rateLimitPepper,
            ["Auth:TokenHash:CurrentPepperVersion"] = "1",
            ["Auth:TokenHash:CurrentPepper"] = tokenPepper,
            ["Auth:TOTP:RecoveryCodePepperVersion"] = "1",
            ["Auth:TOTP:RecoveryCodePepper"] = recoveryCodePepper,
            ["Auth:Login:IpFailuresPerMinute"] = "20",
            ["Auth:Login:RateLimitScopePepper"] = loginRateLimitPepper,
            ["ApiKeys:CurrentPepper"] = apiKeyPepper,
            ["Idempotency:RequestHashPepper"] = idempotencyPepper,
            ["Data:Postgres:ConnectionString"] =
                $"Host=localhost;Database=poolai;Username=poolai;Password={password}",
            ["Data:Redis:ConnectionString"] =
                $"localhost:6379,user=poolai,password={password},abortConnect=false",
            ["Data:Redis:KeyPrefix"] = "poolai:r1:test:",
            ["Email:Smtp:Host"] = "localhost",
            ["Email:Smtp:Security"] = "starttls",
            ["Email:FromAddress"] = "noreply@localhost.test",
            ["Secrets:Envelope:CurrentKeyId"] = "test-kek-v1",
            ["Secrets:Envelope:CurrentKey"] = envelopeKey,
            ["Secrets:Envelope:DecryptKeyRing:test-kek-v1"] = envelopeKey,
            ["Health:Ntp:Server"] = "127.0.0.1",
        };
    }

}
