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
    [InlineData("Auth:RefreshToken:CurrentPepper")]
    [InlineData("Auth:PasswordReset:RateLimitScopePepper")]
    [InlineData("Auth:TokenHash:CurrentPepper")]
    [InlineData("Auth:TOTP:RecoveryCodePepper")]
    [InlineData("Auth:Login:RateLimitScopePepper")]
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

    [Theory]
    [InlineData("sk-a")]
    [InlineData("sk-abcdefghijklmn")]
    [InlineData("SK-pool-")]
    [InlineData("sk-pool.")]
    [InlineData("sk-pøøl-")]
    public void InvalidApiKeyPrefixIsRejected(string prefix)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["ApiKeys:Prefix"] = prefix;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("ApiKeys:Prefix", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("sk-aa")]
    [InlineData("sk-pool-")]
    [InlineData("sk-ABCDEFGHIJKLM")]
    public void ValidApiKeyPrefixIsAccepted(string prefix)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["ApiKeys:Prefix"] = prefix;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("32768")]
    [InlineData("not-a-version")]
    public void InvalidCurrentApiKeyPepperVersionIsRejected(string version)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["ApiKeys:CurrentPepperVersion"] = version;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("ApiKeys:CurrentPepperVersion", exception.InvalidKeys);
    }

    [Fact]
    public void PreviousApiKeyPepperVersionAndSecretMustBeConfiguredTogether()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["ApiKeys:PreviousPepperVersion"] = "2";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("ApiKeys:PreviousPepperVersion", exception.InvalidKeys);
        Assert.Contains("ApiKeys:PreviousPepper", exception.InvalidKeys);
    }

    [Fact]
    public void PreviousApiKeyPepperVersionMustDifferFromCurrent()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["ApiKeys:PreviousPepperVersion"] = "1";
        values["ApiKeys:PreviousPepper"] =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("ApiKeys:PreviousPepperVersion", exception.InvalidKeys);
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
    [InlineData("Quota:DeniedMutationAttemptsPerMinute", "0")]
    [InlineData("Quota:DeniedMutationAttemptsPerMinute", "21")]
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
    [InlineData("Supply:Health:ProbeIntervalSeconds", "31")]
    [InlineData("Supply:Health:ProbeTimeoutSeconds", "11")]
    [InlineData("Supply:Health:ProbeMaxResponseBytes", "1048577")]
    [InlineData("Supply:Health:ProbeMaxConcurrency", "9")]
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
    public void CanonicalTrustedProxyIngressConfigurationIsAccepted()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs:0"] = "10.0.0.0/8";
        values["Gateway:Ingress:TrustedProxyCidrs:1"] = "2001:db8::/64";
        values["Gateway:Ingress:TrustedProxyCidrs:2"] = "192.0.2.7/32";
        values["Gateway:Ingress:TrustedProxyCidrs:3"] = "2001:db8:1::7/128";
        values["Gateway:Ingress:ForwardedForLimit"] = "8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production");
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("10.0.0.1/8")]
    [InlineData("2001:db8::1/64")]
    [InlineData("192.168.001.0/24")]
    [InlineData("192.168.0.0/024")]
    [InlineData("2001:DB8::/64")]
    [InlineData("2001:0db8::/64")]
    [InlineData("fe80::1%1/128")]
    [InlineData("::ffff:192.0.2.1/128")]
    [InlineData("192.0.2.1")]
    [InlineData("192.0.2.0/33")]
    [InlineData("2001:db8::/129")]
    public void NonCanonicalTrustedProxyCidrFailsStartupValidation(string cidr)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs:0"] = cidr;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:Ingress:TrustedProxyCidrs",
            exception.InvalidKeys);
    }

    [Fact]
    public void DuplicateTrustedProxyCidrFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs:0"] = "10.0.0.0/8";
        values["Gateway:Ingress:TrustedProxyCidrs:1"] = "10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:Ingress:TrustedProxyCidrs",
            exception.InvalidKeys);
    }

    [Fact]
    public void MoreThanSixtyFourTrustedProxyCidrsFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        for (int index = 0; index <= 64; index++)
        {
            values[$"Gateway:Ingress:TrustedProxyCidrs:{index}"] =
                $"198.51.100.{index}/32";
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:Ingress:TrustedProxyCidrs",
            exception.InvalidKeys);
    }

    [Theory]
    [InlineData("Gateway:Ingress:ForwardedForLimit", "0")]
    [InlineData("Gateway:Ingress:ForwardedForLimit", "9")]
    [InlineData("Gateway:Ingress:ForwardedForLimit", "not-an-integer")]
    public void InvalidGatewayIngressScalarFailsStartupValidation(
        string key,
        string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void TrustedProxyCidrsMustUseAContiguousArrayShape()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs:1"] = "10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:Ingress:TrustedProxyCidrs",
            exception.InvalidKeys);
    }

    [Fact]
    public void TrustedProxyCidrsRejectAScalarInsteadOfAnArray()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs"] = "10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:Ingress:TrustedProxyCidrs",
            exception.InvalidKeys);
    }

    [Fact]
    public void EstimatorDefaultsCannotExceedMaximumPerAttempt()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:DefaultMaxOutputTokens"] = "4097";
        values["Gateway:MaxEstimatedTokensPerAttempt"] = "4096";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(
                configuration,
                "Production"));

        Assert.Contains(
            "Gateway:DefaultMaxOutputTokens",
            exception.InvalidKeys);
        Assert.Contains(
            "Gateway:MaxEstimatedTokensPerAttempt",
            exception.InvalidKeys);
    }

    [Fact]
    public void WorkerProfileDoesNotConsumeApiIngressConfiguration()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Gateway:Ingress:TrustedProxyCidrs:0"] = "not-a-cidr";
        values["Gateway:Ingress:ForwardedForLimit"] = "9";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production",
            PoolAiRuntimeConfigurationValidator.HostProfile.Worker);
    }

    [Fact]
    public void CanonicalPrivateEgressRulesPassStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:0"] =
            "https://upstream.internal.example:8443|10.20.0.0/16";
        values["Supply:Health:PrivateEgressRules:1"] =
            "https://[fd12:3456::10]|fd12:3456::/32";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" https://upstream.internal.example|10.0.0.0/8")]
    [InlineData("https://upstream internal.example|10.0.0.0/8")]
    [InlineData("https://upstréam.internal.example|10.0.0.0/8")]
    [InlineData("https://upstream.internal.example\\|10.0.0.0/8")]
    [InlineData("https://upstream.internal.example")]
    [InlineData("https://upstream.internal.example||10.0.0.0/8")]
    [InlineData("https://upstream.internal.example|")]
    [InlineData("https://[|10.0.0.0/8")]
    [InlineData("http://upstream.internal.example|10.0.0.0/8")]
    [InlineData("https://upstream.internal.example/v1|10.0.0.0/8")]
    [InlineData("https://upstream.internal.example|10.0.0.0")]
    [InlineData("https://upstream.internal.example|10.0.0.0/")]
    [InlineData("https://upstream.internal.example|10.0.0.0/8/8")]
    [InlineData("https://upstream.internal.example|10.0.0.0/08")]
    [InlineData("https://upstream.internal.example|10.0.0.0/1000")]
    [InlineData("https://upstream.internal.example|10.0.0.0/x")]
    [InlineData("https://upstream.internal.example|fe80::1%1/64")]
    [InlineData("https://upstream.internal.example|not-an-address/8")]
    [InlineData("https://upstream.internal.example|::ffff:10.0.0.0/120")]
    [InlineData("https://upstream.internal.example|10.0.0.0/33")]
    [InlineData("https://upstream.internal.example|fd00::/129")]
    [InlineData("https://upstream.internal.example|8.8.8.0/24")]
    [InlineData("https://upstream.internal.example|10.20.1.1/16")]
    [InlineData("https://upstream.internal.example|10.0.0.0/7")]
    [InlineData("https://upstream.internal.example|127.0.0.0/8")]
    [InlineData("https://upstream.internal.example|2001:db8::/32")]
    [InlineData("https://upstream.internal.example|fc00::/6")]
    [InlineData("https://localhost|10.0.0.0/8")]
    [InlineData("https://127.0.0.1|10.0.0.0/8")]
    [InlineData("https://[::ffff:127.0.0.1]|10.0.0.0/8")]
    [InlineData("https://upstream.internal.example|FD00::/8")]
    public void InvalidPrivateEgressRuleFailsStartupValidation(string rule)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:0"] = rule;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void ScalarPrivateEgressRuleContainerFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules"] =
            "https://upstream.internal.example|10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void MoreThanSixtyFourPrivateEgressRulesFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        for (int index = 0; index < 65; index++)
        {
            values[$"Supply:Health:PrivateEgressRules:{index}"] =
                $"https://upstream-{index}.internal.example|10.0.0.0/8";
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void NestedPrivateEgressRuleEntryFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:0"] =
            "https://upstream.internal.example|10.0.0.0/8";
        values["Supply:Health:PrivateEgressRules:0:Unexpected"] = "value";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void NonNumericPrivateEgressRuleIndexFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:not-an-index"] =
            "https://upstream.internal.example|10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void DuplicatePrivateEgressRuleFailsStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:0"] =
            "https://upstream.internal.example|10.0.0.0/8";
        values["Supply:Health:PrivateEgressRules:1"] =
            "https://upstream.internal.example/|10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
    }

    [Fact]
    public void SparsePrivateEgressRuleIndexesFailStartupValidation()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Supply:Health:PrivateEgressRules:1"] =
            "https://upstream.internal.example|10.0.0.0/8";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production"));

        Assert.Contains(
            "Supply:Health:PrivateEgressRules",
            exception.InvalidKeys);
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

    [Fact]
    public void PreviousRefreshPepperRequiresADistinctVersionAndCompletePair()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Auth:RefreshToken:PreviousPepperVersion"] = "1";
        values["Auth:RefreshToken:PreviousPepper"] =
            values["Auth:RefreshToken:CurrentPepper"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:RefreshToken:PreviousPepperVersion", exception.InvalidKeys);
        Assert.Contains("Auth:RefreshToken:CurrentPepper", exception.InvalidKeys);
        Assert.Contains("Auth:RefreshToken:PreviousPepper", exception.InvalidKeys);
    }

    [Theory]
    [InlineData("Auth:RefreshToken:PreviousPepperVersion", "2")]
    [InlineData("Auth:RefreshToken:PreviousPepper", "not-even-base64")]
    public void PreviousRefreshPepperRejectsAnIncompletePair(string key, string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:RefreshToken:PreviousPepperVersion", exception.InvalidKeys);
        Assert.Contains("Auth:RefreshToken:PreviousPepper", exception.InvalidKeys);
    }

    [Fact]
    public void PreviousRefreshPepperAcceptsACompleteDistinctPair()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Auth:RefreshToken:PreviousPepperVersion"] = "2";
        values["Auth:RefreshToken:PreviousPepper"] =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production");
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

    [Theory]
    [InlineData("Auth:Login:IpFailuresPerMinute", "0")]
    [InlineData("Auth:Login:IpFailuresPerMinute", "101")]
    [InlineData("Auth:Login:MaxFailures", "2")]
    [InlineData("Auth:Login:MaxFailures", "21")]
    [InlineData("Auth:Login:LockoutMinutes", "0")]
    [InlineData("Auth:Login:LockoutMinutes", "1441")]
    public void LoginSecurityLimitsAreBounded(string key, string value)
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

    [Theory]
    [InlineData("Auth:RefreshToken:CurrentPepper")]
    [InlineData("Auth:PasswordReset:RateLimitScopePepper")]
    [InlineData("Auth:TokenHash:CurrentPepper")]
    [InlineData("Auth:TOTP:RecoveryCodePepper")]
    [InlineData("Auth:Login:RateLimitScopePepper")]
    [InlineData("ApiKeys:CurrentPepper")]
    [InlineData("Idempotency:RequestHashPepper")]
    public void SecurityPurposesCannotReuseTheSameKeyMaterial(string key)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = values["Auth:Jwt:SigningKey"];
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception = Assert.Throws<PoolAiConfigurationException>(() =>
            PoolAiRuntimeConfigurationValidator.Validate(configuration, "Production"));

        Assert.Contains("Auth:Jwt:SigningKey", exception.InvalidKeys);
        Assert.Contains(key, exception.InvalidKeys);
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

    [Theory]
    [InlineData("Secrets:Envelope:Rewrap:Enabled", "not-a-boolean")]
    [InlineData("Secrets:Envelope:Rewrap:BatchSize", "0")]
    [InlineData("Secrets:Envelope:Rewrap:BatchSize", "1001")]
    [InlineData("Secrets:Envelope:Rewrap:MaxAttempts", "0")]
    [InlineData("Secrets:Envelope:Rewrap:MaxAttempts", "11")]
    [InlineData("Secrets:Envelope:Rewrap:RetryDelaySeconds", "0")]
    [InlineData("Secrets:Envelope:Rewrap:RetryDelaySeconds", "61")]
    public void WorkerCredentialRewrapConfigurationFailsClosed(
        string key,
        string value)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values[key] = value;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production",
                    PoolAiRuntimeConfigurationValidator.HostProfile.Worker));

        Assert.Contains(key, exception.InvalidKeys);
    }

    [Fact]
    public void EnabledWorkerCredentialRewrapRequiresAHistoricalEnvelopeKey()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Secrets:Envelope:Rewrap:Enabled"] = "true";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiConfigurationException exception =
            Assert.Throws<PoolAiConfigurationException>(() =>
                PoolAiRuntimeConfigurationValidator.Validate(
                    configuration,
                    "Production",
                    PoolAiRuntimeConfigurationValidator.HostProfile.Worker));

        Assert.Contains(
            "Secrets:Envelope:DecryptKeyRing",
            exception.InvalidKeys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void DisabledWorkerCredentialRewrapAllowsOnlyTheCurrentEnvelopeKey(
        string? enabled)
    {
        Dictionary<string, string?> values = ValidConfiguration();
        if (enabled is not null)
        {
            values["Secrets:Envelope:Rewrap:Enabled"] = enabled;
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production",
            PoolAiRuntimeConfigurationValidator.HostProfile.Worker);
    }

    [Fact]
    public void EnabledWorkerCredentialRewrapAcceptsTwoDistinctEnvelopeKeys()
    {
        Dictionary<string, string?> values = ValidConfiguration();
        values["Secrets:Envelope:Rewrap:Enabled"] = "true";
        values["Secrets:Envelope:DecryptKeyRing:retired-kek"] =
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        PoolAiRuntimeConfigurationValidator.Validate(
            configuration,
            "Production",
            PoolAiRuntimeConfigurationValidator.HostProfile.Worker);
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
            ["App:PublicBaseUrl"] = "https://poolai.example.test",
            ["App:TimeZone"] = "Asia/Shanghai",
            ["App:AllowedHosts:0"] = "poolai.example.test",
            ["Cors:AllowedOrigins:0"] = "https://poolai.example.test",
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
            ["ApiKeys:CurrentPepperVersion"] = "1",
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
