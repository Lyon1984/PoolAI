using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using PoolAI.Modules.Operations;
using PoolAI.Modules.Operations.Infrastructure;
using PoolAI.Modules.Operations.Infrastructure.Configuration;

namespace PoolAI.UnitTests;

public sealed class ConfigurationValidationTests
{
    [Theory]
    [InlineData("Auth:Jwt:SigningKey")]
    [InlineData("Auth:PasswordReset:RateLimitScopePepper")]
    [InlineData("Auth:TokenHash:CurrentPepper")]
    [InlineData("Idempotency:RequestHashPepper")]
    [InlineData("ApiKeys:CurrentPepper")]
    [InlineData("Secrets:Envelope:CurrentKey")]
    public void MissingCriticalSecretFailsStartupValidation(string key)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values.Remove(key);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void ProductionWildcardCorsIsRejected()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Cors:AllowedOrigins:0"] = "*";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Cors:AllowedOrigins", exception.InvalidKeys);
    }

    [Fact]
    public void InvalidIanaTimezoneIsRejected()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["App:TimeZone"] = "Invalid/PoolAI-Time-Zone";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("App:TimeZone", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("https://time.example.test")]
    [InlineData("user@time.example.test")]
    [InlineData("time.example.test:123")]
    [InlineData("time.example.test/path")]
    [InlineData("time.example.test?pool=1")]
    [InlineData("time.example.test#fragment")]
    public void NtpServerRejectsAnythingOtherThanAHostOrIpLiteral(string server)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Health:Ntp:Server"] = server;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Health:Ntp:Server", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("time.example.test")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    public void NtpServerAcceptsAHostOrIpLiteral(string server)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Health:Ntp:Server"] = server;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
    }

    [Theory]
    [InlineData("Health:Ntp:Port", "0")]
    [InlineData("Health:Ntp:Port", "65536")]
    [InlineData("Health:Ntp:TimeoutMilliseconds", "99")]
    [InlineData("Health:Ntp:TimeoutMilliseconds", "2501")]
    public void NtpNumericBoundsAreValidated(string key, string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void MissingNtpServerFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values.Remove("Health:Ntp:Server");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Health:Ntp:Server", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    [InlineData("https://smtp.example.test")]
    [InlineData("smtp.example.test/path")]
    [InlineData("smtp.example.test?tls=true")]
    public void SmtpHostRejectsIpLiteralsAndUriShapes(string host)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Email:Smtp:Host"] = host;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Email:Smtp:Host", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("smtp.example.test")]
    [InlineData("mock-smtp")]
    public void SmtpHostAcceptsDnsNames(string host)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Email:Smtp:Host"] = host;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
    }

    [Theory]
    [InlineData("PoolAI <no-reply@poolai.example.test>")]
    [InlineData(" no-reply@poolai.example.test")]
    [InlineData("\"no-reply\"@poolai.example.test")]
    [InlineData("nö-reply@poolai.example.test")]
    [InlineData("no..reply@poolai.example.test")]
    [InlineData("no-reply@[127.0.0.1]")]
    [InlineData("no-reply@invalid_domain.test")]
    public void EmailFromAddressRejectsMailboxesTheWorkerCannotDeliver(string address)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Email:FromAddress"] = address;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Email:FromAddress", exception.InvalidKeys);
    }

    [Fact]
    public void EmailFromAddressAcceptsIdnaDomainNormalizedByTheWorker()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Email:FromAddress"] = "no-reply@BÜCHER.Example";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("poolai:r1:Test:")]
    [InlineData("poolai:r1:test_:")]
    [InlineData("poolai:r1:test")]
    [InlineData("poolai:r1::")]
    [InlineData("poolai:r1:abcdefghijklmnopqrstuvwxyz1234567:")]
    public void RedisKeyPrefixRejectsValuesOutsideTheFrozenShape(string prefix)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Data:Redis:KeyPrefix"] = prefix;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Data:Redis:KeyPrefix", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("poolai:r1:test:")]
    [InlineData("poolai:r1:local-compose:")]
    [InlineData("poolai:r1:0:")]
    [InlineData("poolai:r1:abcdefghijklmnopqrstuvwxyz123456:")]
    public void RedisKeyPrefixAcceptsTheFrozenShape(string prefix)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Data:Redis:KeyPrefix"] = prefix;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
    }

    [Fact]
    public void MissingRedisKeyPrefixUsesTheSameEnvironmentDefaultAsValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values.Remove("Data:Redis:KeyPrefix");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Test");
        RuntimeDependencyOptions options = DependencyInjection
            .CreateRuntimeDependencyOptions(configuration, "Test");
        Assert.Equal("poolai:r1:test:", options.RedisKeyPrefix);
    }

    [Theory]
    [InlineData("Quota:StreamLeaseSeconds", "121")]
    [InlineData("Quota:ReservationSweepSeconds", "31")]
    [InlineData("Quota:MaxStreamSeconds", "7199")]
    [InlineData("Quota:DisconnectDrainSeconds", "16")]
    [InlineData("Admission:DataQueueLimit", "1")]
    [InlineData("Admission:ControlQueueLimit", "51")]
    [InlineData("Admission:UsageQueueLimit", "21")]
    [InlineData("Routing:Breaker:SamplingSeconds", "31")]
    [InlineData("Routing:Breaker:MinimumThroughput", "11")]
    [InlineData("Routing:Breaker:FailureRatio", "0.51")]
    [InlineData("Routing:Breaker:ConsecutiveFailures", "6")]
    [InlineData("Routing:Breaker:InitialBreakSeconds", "31")]
    [InlineData("Routing:Breaker:MaxBreakSeconds", "301")]
    [InlineData("Routing:Breaker:HalfOpenProbeSeconds", "11")]
    [InlineData("Routing:Breaker:SuccessesToClose", "3")]
    [InlineData("Usage:CacheSeconds", "16")]
    [InlineData("Usage:MaximumReportedLagSeconds", "61")]
    public void FrozenRuntimeBoundsRejectContractDrift(string key, string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void NtpTimeoutMustBeLessThanTheReadinessTimeout()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Health:Ntp:TimeoutMilliseconds"] = "1000";
        values["Health:ReadinessTimeoutSeconds"] = "1";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Health:Ntp:TimeoutMilliseconds", exception.InvalidKeys);
    }

    [Fact]
    public void FailureMessageContainsKeysButNotSecretValues()
    {
        const string SensitiveValue = "not-a-valid-secret-but-still-sensitive";
        Dictionary<string, string?> values = ValidConfiguration();
        values["Auth:Jwt:SigningKey"] = SensitiveValue;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:Jwt:SigningKey", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviousTokenPepperRequiresADistinctVersionAndCompletePair()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Auth:TokenHash:PreviousPepperVersion"] = "1";
        values["Auth:TokenHash:PreviousPepper"] = values["Auth:TokenHash:CurrentPepper"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:TokenHash:PreviousPepperVersion", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("Auth:PasswordReset:IpRequestsPerMinute", "0")]
    [InlineData("Auth:PasswordReset:AccountRequestsPerMinute", "21")]
    public void PasswordResetRateLimitsAreBounded(string key, string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void SecurityPurposesCannotReuseTheSameKeyMaterial()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Auth:TokenHash:CurrentPepper"] = values["Auth:Jwt:SigningKey"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:Jwt:SigningKey", exception.InvalidKeys);
        Assert.Contains("Auth:TokenHash:CurrentPepper", exception.InvalidKeys);
    }

    [Fact]
    public void ApiPurposeSecretCannotReuseAnyHistoricalEnvelopeRingKey()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Secrets:Envelope:DecryptKeyRing:retired-kek"] =
            values["Idempotency:RequestHashPepper"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Idempotency:RequestHashPepper", exception.InvalidKeys);
        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing:retired-kek",
            exception.InvalidKeys);
    }

    [Fact]
    public void EnvelopeCurrentKeyMustMatchTheRingEntrySelectedByCurrentKeyId()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Secrets:Envelope:DecryptKeyRing:test-kek-v1"] =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Secrets:Envelope:CurrentKey", exception.InvalidKeys);
        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing:test-kek-v1",
            exception.InvalidKeys);
    }

    [Fact]
    public void EnvelopeKeysMustBeExactly256Bits()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        string oversizedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(33));
        values["Secrets:Envelope:CurrentKey"] = oversizedKey;
        values["Secrets:Envelope:DecryptKeyRing:test-kek-v1"] = oversizedKey;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Secrets:Envelope:CurrentKey", exception.InvalidKeys);
        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing:test-kek-v1",
            exception.InvalidKeys);
    }

    [Fact]
    public void EnvelopeRingKeysMustContainDistinctKeyMaterial()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Secrets:Envelope:DecryptKeyRing:retired-kek"] =
            values["Secrets:Envelope:DecryptKeyRing:test-kek-v1"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production",
                PoolAiRuntimeConfigurationValidator.HostProfile.Worker));

        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing:test-kek-v1",
            exception.InvalidKeys);
        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing:retired-kek",
            exception.InvalidKeys);
    }

    [Fact]
    public void WorkerProfileDoesNotRequireApiAuthenticationConfiguration()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        foreach (string key in values.Keys
                     .Where(static key =>
                         key.StartsWith("Auth:", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("ApiKeys:", StringComparison.OrdinalIgnoreCase)
                         || key.StartsWith("Idempotency:", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            values.Remove(key);
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production",
            PoolAiRuntimeConfigurationValidator.HostProfile.Worker);
    }

    [Theory]
    [InlineData("Data:Postgres:ConnectionString")]
    [InlineData("Data:Redis:ConnectionString")]
    [InlineData("Email:Smtp:Host")]
    [InlineData("Secrets:Envelope:CurrentKey")]
    public void WorkerProfileStillRequiresItsRuntimeInputs(string key)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values.Remove(key);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production",
                PoolAiRuntimeConfigurationValidator.HostProfile.Worker));

        Assert.Contains(key, exception.InvalidKeys);
    }

    internal static Dictionary<string, string?> ValidConfiguration()
    {
        string jwtSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string rateLimitPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string tokenPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string apiKeyPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string idempotencyPepper = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string envelopeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["App:PublicBaseUrl"] = "https://poolai.example.test",
            ["App:TimeZone"] = "Asia/Shanghai",
            ["App:AllowedHosts:0"] = "poolai.example.test",
            ["Cors:AllowedOrigins:0"] = "https://poolai.example.test",
            ["Auth:Jwt:SigningKey"] = jwtSecret,
            ["Auth:PasswordReset:RateLimitScopePepper"] = rateLimitPepper,
            ["Auth:TokenHash:CurrentPepperVersion"] = "1",
            ["Auth:TokenHash:CurrentPepper"] = tokenPepper,
            ["ApiKeys:CurrentPepper"] = apiKeyPepper,
            ["Idempotency:RequestHashPepper"] = idempotencyPepper,
            ["Data:Postgres:ConnectionString"] =
                $"Host=postgres;Database=poolai;Username=poolai;Password={password};SSL Mode=Require;Trust Server Certificate=true",
            ["Data:Redis:ConnectionString"] =
                $"redis:6379,user=poolai,password={password},ssl=true,abortConnect=false",
            ["Data:Redis:KeyPrefix"] = "poolai:r1:test:",
            ["Email:Smtp:Host"] = "mock-smtp",
            ["Email:Smtp:Security"] = "starttls",
            ["Email:Smtp:Username"] = "poolai",
            ["Email:Smtp:Password"] = password,
            ["Email:FromAddress"] = "noreply@poolai.example.test",
            ["Secrets:Envelope:CurrentKeyId"] = "test-kek-v1",
            ["Secrets:Envelope:CurrentKey"] = envelopeKey,
            ["Secrets:Envelope:DecryptKeyRing:test-kek-v1"] = envelopeKey,
            ["Health:Ntp:Server"] = "time.poolai.example.test",
        };
    }
}
