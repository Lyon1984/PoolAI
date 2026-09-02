using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;

namespace PoolAI.Modules.Operations.Infrastructure.Configuration;

public static class PoolAiRuntimeConfigurationValidator
{
    public enum HostProfile
    {
        Api,
        Worker,
    }

    private const long JavaScriptSafeIntegerMax = 9_007_199_254_740_991;
    private const string PrivateEgressRulesKey =
        "Supply:Health:PrivateEgressRules";
    private const int MaximumPrivateEgressRules = 64;
    private const string TrustedProxyCidrsKey =
        "Gateway:Ingress:TrustedProxyCidrs";
    private const int MaximumTrustedProxyCidrs = 64;

    private static readonly string[] ForbiddenSections =
    [
        "Payment",       // poolai-forbidden-scope-guard
        "Billing",       // poolai-forbidden-scope-guard
        "Pricing",       // poolai-forbidden-scope-guard
        "Balance",       // poolai-forbidden-scope-guard
        "Refund",        // poolai-forbidden-scope-guard
        "Promo",         // poolai-forbidden-scope-guard
        "Redeem",        // poolai-forbidden-scope-guard
        "Affiliate",     // poolai-forbidden-scope-guard
        "Commission",    // poolai-forbidden-scope-guard
        "PersonalQuota", // poolai-forbidden-scope-guard
        "UserQuota",     // poolai-forbidden-scope-guard
    ];

    public static void Validate(
        IConfiguration configuration,
        string environmentName,
        HostProfile hostProfile = HostProfile.Api)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        if (hostProfile is not HostProfile.Api
            and not HostProfile.Worker)
        {
            throw new ArgumentOutOfRangeException(nameof(hostProfile), hostProfile, null);
        }

        Validation validation = new(configuration);
        bool isProduction = string.Equals(
            environmentName,
            "Production",
            StringComparison.OrdinalIgnoreCase);

        ValidateApplication(validation, isProduction);
        if (hostProfile is HostProfile.Api)
        {
            ValidateAuthentication(validation);
        }

        ValidateDataStores(validation, isProduction, environmentName);
        ValidateEmail(validation, isProduction);
        string[] envelopeRingKeyPaths = ValidateEnvelope(validation);
        if (hostProfile is HostProfile.Worker)
        {
            ValidateAccountCredentialRewrap(
                validation,
                envelopeRingKeyPaths.Length);
        }

        ValidateSecretIsolation(validation, hostProfile, envelopeRingKeyPaths);

        ValidateOutbox(validation);
        ValidateQuotaAndGateway(validation);
        if (hostProfile is HostProfile.Api)
        {
            ValidateGatewayIngress(validation);
        }

        ValidateAdmissionAndRouting(validation);
        ValidateUsageAndOperations(validation);
        ValidateForbiddenConfiguration(validation, configuration);
        validation.ThrowIfInvalid();
    }

    private static void ValidateSecretIsolation(
        Validation validation,
        HostProfile hostProfile,
        string[] envelopeRingKeyPaths)
    {
        if (hostProfile is not HostProfile.Api)
        {
            validation.RequireDistinctBase64Secrets(envelopeRingKeyPaths);
            return;
        }

        validation.RequireDistinctBase64Secrets(
        [
            "Auth:Jwt:SigningKey",
            "Auth:RefreshToken:CurrentPepper",
            "Auth:RefreshToken:PreviousPepper",
            "Auth:PasswordReset:RateLimitScopePepper",
            "Auth:TokenHash:CurrentPepper",
            "Auth:TokenHash:PreviousPepper",
            "Auth:TOTP:RecoveryCodePepper",
            "Auth:Login:RateLimitScopePepper",
            "Idempotency:RequestHashPepper",
            "ApiKeys:CurrentPepper",
            "ApiKeys:PreviousPepper",
            .. envelopeRingKeyPaths,
        ]);
    }

    private static void ValidateApplication(Validation validation, bool isProduction)
    {
        string publicBaseUrl = validation.Required("App:PublicBaseUrl");
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out Uri? publicUri)
            || publicUri.Query.Length != 0
            || publicUri.Fragment.Length != 0
            || (isProduction
                && !string.Equals(
                    publicUri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)))
        {
            validation.Invalid("App:PublicBaseUrl");
        }

        string timeZone = validation.String("App:TimeZone", "Asia/Shanghai");
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            validation.Invalid("App:TimeZone");
        }
        catch (InvalidTimeZoneException)
        {
            validation.Invalid("App:TimeZone");
        }

        string[] allowedHosts = validation.StringArray("App:AllowedHosts");
        if (allowedHosts.Length == 0
            || allowedHosts.Any(static host => string.IsNullOrWhiteSpace(host))
            || (isProduction && allowedHosts.Contains("*", StringComparer.Ordinal)))
        {
            validation.Invalid("App:AllowedHosts");
        }

        string[] origins = validation.StringArray("Cors:AllowedOrigins", required: false);
        if (origins.Any(origin => !IsExactOrigin(origin))
            || (isProduction && origins.Contains("*", StringComparer.Ordinal)))
        {
            validation.Invalid("Cors:AllowedOrigins");
        }
    }

    private static void ValidateAuthentication(Validation validation)
    {
        validation.Length("Auth:Jwt:Issuer", "PoolAI", 1, 128);
        validation.Length("Auth:Jwt:Audience", "PoolAI.Web", 1, 128);
        validation.Fixed("Auth:Jwt:AccessTokenMinutes", 15);
        validation.Fixed("Auth:Jwt:RefreshTokenDays", 30);
        validation.Fixed("Auth:Jwt:ClockSkewSeconds", 30);
        validation.Base64Secret("Auth:Jwt:SigningKey", 32);
        ValidateRefreshTokenHash(validation);
        validation.Range("Auth:Password:MinLength", 12, 12, 128);
        validation.Range("Auth:PasswordReset:TokenMinutes", 30, 5, 60);
        validation.Range("Auth:PasswordReset:IpRequestsPerMinute", 5, 1, 60);
        validation.Range("Auth:PasswordReset:AccountRequestsPerMinute", 3, 1, 20);
        validation.Base64Secret("Auth:PasswordReset:RateLimitScopePepper", 32);
        ValidateOneTimeTokenHash(validation);
        ValidateTotpAndLogin(validation);

        string prefix = validation.String("ApiKeys:Prefix", "sk-pool-");
        if (!Regex.IsMatch(
                prefix,
                "^sk-[A-Za-z0-9_-]{2,13}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
        {
            validation.Invalid("ApiKeys:Prefix");
        }

        int currentApiKeyPepperVersion = validation.Range(
            "ApiKeys:CurrentPepperVersion",
            1,
            1,
            short.MaxValue);
        validation.Base64Secret("ApiKeys:CurrentPepper", 32);
        ValidateOptionalPepperPair(
            validation,
            "ApiKeys",
            currentApiKeyPepperVersion);
        validation.Base64Secret("Idempotency:RequestHashPepper", 32);
    }

    private static void ValidateRefreshTokenHash(Validation validation)
    {
        int currentRefreshPepperVersion = validation.Range(
            "Auth:RefreshToken:CurrentPepperVersion",
            1,
            1,
            short.MaxValue);
        validation.Base64Secret("Auth:RefreshToken:CurrentPepper", 32);
        ValidateOptionalPepperPair(
            validation,
            "Auth:RefreshToken",
            currentRefreshPepperVersion);
    }

    private static void ValidateOneTimeTokenHash(Validation validation)
    {
        int currentTokenPepperVersion = validation.Range(
            "Auth:TokenHash:CurrentPepperVersion",
            1,
            1,
            short.MaxValue);
        validation.Base64Secret("Auth:TokenHash:CurrentPepper", 32);
        string? previousTokenPepperVersion = validation.Optional(
            "Auth:TokenHash:PreviousPepperVersion");
        string? previousTokenPepper = validation.Optional("Auth:TokenHash:PreviousPepper");
        if (string.IsNullOrWhiteSpace(previousTokenPepperVersion)
            != string.IsNullOrWhiteSpace(previousTokenPepper))
        {
            validation.Invalid("Auth:TokenHash:PreviousPepperVersion");
            validation.Invalid("Auth:TokenHash:PreviousPepper");
        }
        else if (!string.IsNullOrWhiteSpace(previousTokenPepperVersion))
        {
            if (!int.TryParse(
                    previousTokenPepperVersion,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedPreviousVersion)
                || parsedPreviousVersion is < 1 or > short.MaxValue
                || parsedPreviousVersion == currentTokenPepperVersion)
            {
                validation.Invalid("Auth:TokenHash:PreviousPepperVersion");
            }

            validation.OptionalBase64Secret("Auth:TokenHash:PreviousPepper", 32);
        }
    }

    private static void ValidateTotpAndLogin(Validation validation)
    {
        validation.Length("Auth:TOTP:Issuer", "PoolAI", 1, 64);
        validation.Fixed("Auth:TOTP:StepSeconds", 30);
        validation.Fixed("Auth:TOTP:AllowedAdjacentSteps", 1);
        validation.Range(
            "Auth:TOTP:RecoveryCodePepperVersion",
            1,
            1,
            short.MaxValue);
        validation.Base64Secret("Auth:TOTP:RecoveryCodePepper", 32);
        validation.Range("Auth:Login:IpFailuresPerMinute", 20, 1, 100);
        validation.Base64Secret("Auth:Login:RateLimitScopePepper", 32);
        validation.Range("Auth:Login:MaxFailures", 5, 3, 20);
        validation.Range("Auth:Login:LockoutMinutes", 15, 1, 1_440);
    }

    private static void ValidateOptionalPepperPair(
        Validation validation,
        string section,
        int currentVersion)
    {
        string versionKey = $"{section}:PreviousPepperVersion";
        string pepperKey = $"{section}:PreviousPepper";
        string? version = validation.Optional(versionKey);
        string? pepper = validation.Optional(pepperKey);
        if (string.IsNullOrWhiteSpace(version) != string.IsNullOrWhiteSpace(pepper))
        {
            validation.Invalid(versionKey);
            validation.Invalid(pepperKey);
            return;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        if (!int.TryParse(
                version,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedVersion)
            || parsedVersion is < 1 or > short.MaxValue
            || parsedVersion == currentVersion)
        {
            validation.Invalid(versionKey);
        }

        validation.OptionalBase64Secret(pepperKey, 32);
    }

    private static void ValidateDataStores(
        Validation validation,
        bool isProduction,
        string environmentName)
    {
        string postgres = validation.Required("Data:Postgres:ConnectionString");
        if (!string.IsNullOrWhiteSpace(postgres))
        {
            try
            {
                NpgsqlConnectionStringBuilder builder = new(postgres);
                if (string.IsNullOrWhiteSpace(builder.Host)
                    || string.IsNullOrWhiteSpace(builder.Username)
                    || (isProduction
                        && (builder.SslMode is not SslMode.Require
                            and not SslMode.VerifyCA
                            and not SslMode.VerifyFull
                            || string.IsNullOrWhiteSpace(builder.Password))))
                {
                    validation.Invalid("Data:Postgres:ConnectionString");
                }
            }
            catch (ArgumentException)
            {
                validation.Invalid("Data:Postgres:ConnectionString");
            }
        }

        validation.Range("Data:Postgres:CommandTimeoutSeconds", 30, 5, 120);
        validation.Range("Data:Postgres:MaxPoolSize", 100, 10, 500);

        string redis = validation.Required("Data:Redis:ConnectionString");
        if (!string.IsNullOrWhiteSpace(redis))
        {
            try
            {
                ConfigurationOptions options = ConfigurationOptions.Parse(redis);
                if (options.EndPoints.Count == 0
                    || (isProduction && (!options.Ssl || string.IsNullOrWhiteSpace(options.Password))))
                {
                    validation.Invalid("Data:Redis:ConnectionString");
                }
            }
            catch (ArgumentException)
            {
                validation.Invalid("Data:Redis:ConnectionString");
            }
        }

        string defaultPrefix = PoolAiRuntimeConfigurationDefaults.RedisKeyPrefix(environmentName);
        string keyPrefix = validation.String("Data:Redis:KeyPrefix", defaultPrefix);
        if (!IsRedisKeyPrefix(keyPrefix))
        {
            validation.Invalid("Data:Redis:KeyPrefix");
        }
    }

    private static void ValidateEmail(Validation validation, bool isProduction)
    {
        string host = validation.Required("Email:Smtp:Host");
        if (!string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) != UriHostNameType.Dns)
        {
            validation.Invalid("Email:Smtp:Host");
        }

        validation.Range("Email:Smtp:Port", 587, 1, 65_535);
        string security = validation.String("Email:Smtp:Security", "starttls");
        if ((!string.Equals(security, "starttls", StringComparison.Ordinal)
                && !string.Equals(security, "tls", StringComparison.Ordinal))
            || (isProduction
                && string.Equals(security, "plaintext", StringComparison.Ordinal)))
        {
            validation.Invalid("Email:Smtp:Security");
        }

        string? username = validation.Optional("Email:Smtp:Username");
        string? password = validation.Optional("Email:Smtp:Password");
        if (string.IsNullOrWhiteSpace(username) != string.IsNullOrWhiteSpace(password))
        {
            validation.Invalid("Email:Smtp:Username");
            validation.Invalid("Email:Smtp:Password");
        }

        string fromAddress = validation.Required("Email:FromAddress");
        if (!string.IsNullOrWhiteSpace(fromAddress) && !IsSupportedMailbox(fromAddress))
        {
            validation.Invalid("Email:FromAddress");
        }

        string fromName = validation.String("Email:FromName", "PoolAI");
        if (fromName.Length is < 1 or > 128 || fromName.Contains('\r') || fromName.Contains('\n'))
        {
            validation.Invalid("Email:FromName");
        }

        validation.Range("Email:Outbox:MaxAttempts", 8, 1, 20);
        validation.Range("Email:Outbox:PollSeconds", 5, 1, 60);
        validation.Range("Email:Outbox:ClaimSeconds", 30, 10, 300);
    }

    private static string[] ValidateEnvelope(Validation validation)
    {
        string currentKeyId = validation.Required("Secrets:Envelope:CurrentKeyId");
        validation.Base64SecretExact("Secrets:Envelope:CurrentKey", 32);
        validation.Fixed("Secrets:Envelope:SchemaVersion", 1);

        string algorithm = validation.String(
            "Secrets:Envelope:Algorithm",
            "A256GCM+A256GCM-v1");
        if (!string.Equals(algorithm, "A256GCM+A256GCM-v1", StringComparison.Ordinal))
        {
            validation.Invalid("Secrets:Envelope:Algorithm");
        }

        IConfigurationSection ring = validation.Configuration
            .GetSection("Secrets:Envelope:DecryptKeyRing");
        IConfigurationSection[] children = ring.GetChildren().ToArray();
        IConfigurationSection? currentRingKey = children.FirstOrDefault(child =>
            string.Equals(child.Key, currentKeyId, StringComparison.Ordinal));
        if (children.Length == 0
            || string.IsNullOrWhiteSpace(currentKeyId)
            || currentRingKey is null)
        {
            validation.Invalid("Secrets:Envelope:DecryptKeyRing");
        }

        foreach (IConfigurationSection child in children)
        {
            validation.Base64SecretExact(child.Path, 32);
        }

        if (currentRingKey is not null)
        {
            validation.RequireEqualBase64Secrets(
                "Secrets:Envelope:CurrentKey",
                currentRingKey.Path);
        }

        return children.Select(static child => child.Path).ToArray();
    }

    private static void ValidateAccountCredentialRewrap(
        Validation validation,
        int envelopeRingKeyCount)
    {
        bool enabled = validation.Boolean(
            "Secrets:Envelope:Rewrap:Enabled",
            false);
        validation.Range("Secrets:Envelope:Rewrap:BatchSize", 100, 1, 1000);
        validation.Range("Secrets:Envelope:Rewrap:MaxAttempts", 3, 1, 10);
        validation.Range(
            "Secrets:Envelope:Rewrap:RetryDelaySeconds",
            5,
            1,
            60);
        if (enabled && envelopeRingKeyCount < 2)
        {
            validation.Invalid("Secrets:Envelope:DecryptKeyRing");
        }
    }

    private static void ValidateOutbox(Validation validation)
    {
        validation.Range("Outbox:MaxAttempts", 12, 1, 50);
        validation.Range("Outbox:PollSeconds", 1, 1, 30);
        validation.Range("Outbox:ClaimSeconds", 30, 10, 300);
        int retryBase = validation.Range("Outbox:RetryBaseSeconds", 1, 1, 86_400);
        int retryMax = validation.Range("Outbox:RetryMaxSeconds", 300, 1, 86_400);
        if (retryMax < retryBase)
        {
            validation.Invalid("Outbox:RetryMaxSeconds");
        }
    }

    private static void ValidateQuotaAndGateway(Validation validation)
    {
        validation.FixedLong("Quota:MaxTotalTokens", JavaScriptSafeIntegerMax);
        validation.Fixed("Quota:NonStreamLeaseSeconds", 300);
        validation.Fixed("Quota:NonStreamRenewEverySeconds", 60);
        validation.Fixed("Quota:MaxNonStreamSeconds", 600);
        validation.Fixed("Quota:StreamLeaseSeconds", 120);
        validation.Fixed("Quota:StreamRenewEverySeconds", 30);
        validation.Fixed("Quota:ReservationSweepSeconds", 30);
        validation.Fixed("Quota:MaxStreamSeconds", 7_200);
        validation.Range("Quota:DisconnectDrainSeconds", 15, 5, 15);
        validation.Range("Quota:DeniedMutationAttemptsPerMinute", 5, 1, 20);

        int defaultMaxOutputTokens = validation.Range(
            "Gateway:DefaultMaxOutputTokens",
            4_096,
            1,
            int.MaxValue);
        long maximumEstimatedTokensPerAttempt = validation.RangeLong(
            "Gateway:MaxEstimatedTokensPerAttempt",
            2_000_000,
            1,
            JavaScriptSafeIntegerMax);
        if (defaultMaxOutputTokens > maximumEstimatedTokensPerAttempt)
        {
            validation.Invalid("Gateway:DefaultMaxOutputTokens");
            validation.Invalid("Gateway:MaxEstimatedTokensPerAttempt");
        }

        validation.Range("Gateway:MaxAttempts", 3, 1, 5);
        validation.Range("Gateway:ConnectTimeoutSeconds", 10, 1, 60);
        validation.Range("Gateway:FirstByteTimeoutSeconds", 60, 5, 300);
        validation.Range("Gateway:StreamIdleTimeoutSeconds", 120, 15, 600);
        int retryBase = validation.Range("Gateway:RetryBaseDelayMs", 200, 1, 60_000);
        int retryMax = validation.Range("Gateway:RetryMaxDelayMs", 2_000, 1, 60_000);
        if (retryMax < retryBase)
        {
            validation.Invalid("Gateway:RetryMaxDelayMs");
        }

        validation.Range("Gateway:RetryBudgetPerSecond", 20, 1, 1_000);
        validation.Range("Gateway:MaxConnectionsPerServer", 256, 16, 4_096);
        validation.RangeLong(
            "Gateway:MaxRequestBodyBytes",
            16_777_216,
            1_048_576,
            33_554_432);
    }

    private static void ValidateGatewayIngress(Validation validation)
    {
        validation.Range(
            "Gateway:Ingress:ForwardedForLimit",
            1,
            1,
            8);

        IConfigurationSection section = validation.Configuration.GetSection(
            TrustedProxyCidrsKey);
        IConfigurationSection[] entries = [.. section.GetChildren()];
        if (section.Value is not null
            || entries.Length > MaximumTrustedProxyCidrs)
        {
            validation.Invalid(TrustedProxyCidrsKey);
            return;
        }

        HashSet<int> indexes = [];
        HashSet<string> canonicalCidrs = new(StringComparer.Ordinal);
        foreach (IConfigurationSection entry in entries)
        {
            if (!TryParseCanonicalArrayIndex(
                    entry.Key,
                    MaximumTrustedProxyCidrs,
                    out int index)
                || entry.Value is null
                || entry.GetChildren().Any()
                || !TryParseCanonicalTrustedProxyCidr(
                    entry.Value,
                    out string canonicalCidr)
                || !indexes.Add(index)
                || !canonicalCidrs.Add(canonicalCidr))
            {
                validation.Invalid(TrustedProxyCidrsKey);
            }
        }

        if (indexes.Count != entries.Length
            || !indexes.Order().SequenceEqual(
                Enumerable.Range(0, indexes.Count)))
        {
            validation.Invalid(TrustedProxyCidrsKey);
        }
    }

    private static void ValidateAdmissionAndRouting(Validation validation)
    {
        validation.Range("Admission:DataNonStreamPermits", 200, 1, 10_000);
        validation.Range("Admission:DataStreamPermits", 600, 1, 10_000);
        validation.Fixed("Admission:DataQueueLimit", 0);
        validation.Range("Admission:ControlPermits", 100, 1, 1_000);
        validation.Range("Admission:ControlQueueLimit", 50, 0, 50);
        validation.Range("Admission:UsagePermits", 100, 1, 1_000);
        validation.Range("Admission:UsageQueueLimit", 20, 0, 20);

        validation.Fixed("Routing:Breaker:SamplingSeconds", 30);
        validation.Fixed("Routing:Breaker:MinimumThroughput", 10);
        validation.FixedDecimal("Routing:Breaker:FailureRatio", 0.50m);
        validation.Fixed("Routing:Breaker:ConsecutiveFailures", 5);
        validation.Fixed("Routing:Breaker:InitialBreakSeconds", 30);
        validation.Fixed("Routing:Breaker:MaxBreakSeconds", 300);
        validation.Fixed("Routing:Breaker:HalfOpenProbeSeconds", 10);
        validation.Fixed("Routing:Breaker:SuccessesToClose", 2);

        validation.Fixed("Supply:Health:ProbeIntervalSeconds", 30);
        validation.Fixed("Supply:Health:ProbeTimeoutSeconds", 10);
        validation.Fixed("Supply:Health:ProbeMaxResponseBytes", 1_048_576);
        validation.Fixed("Supply:Health:ProbeMaxConcurrency", 8);
        ValidatePrivateEgressRules(validation);
    }

    private static void ValidateUsageAndOperations(Validation validation)
    {
        validation.Range("Usage:AggregateIntervalSeconds", 15, 5, 60);
        validation.Range("Usage:CacheSeconds", 15, 0, 15);
        validation.Range("Usage:MaximumReportedLagSeconds", 60, 15, 60);

        string? endpoint = validation.Optional("Observability:Otlp:Endpoint");
        if (!string.IsNullOrWhiteSpace(endpoint)
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri)
                || (!string.Equals(
                        uri.Scheme,
                        Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        uri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase))))
        {
            validation.Invalid("Observability:Otlp:Endpoint");
        }

        validation.Length("Observability:ServiceName", "poolai-api", 1, 64);

        string ntpServer = validation.Required("Health:Ntp:Server");
        if (!string.IsNullOrWhiteSpace(ntpServer) && !IsHostNameOrIpLiteral(ntpServer))
        {
            validation.Invalid("Health:Ntp:Server");
        }

        validation.Range("Health:Ntp:Port", 123, 1, 65_535);
        int ntpTimeout = validation.Range(
            "Health:Ntp:TimeoutMilliseconds",
            750,
            100,
            2_500);
        int readinessTimeout = validation.Range(
            "Health:ReadinessTimeoutSeconds",
            3,
            1,
            10);
        if (ntpTimeout >= readinessTimeout * 1_000)
        {
            validation.Invalid("Health:Ntp:TimeoutMilliseconds");
        }
    }

    private static void ValidateForbiddenConfiguration(
        Validation validation,
        IConfiguration configuration)
    {
        foreach (KeyValuePair<string, string?> pair in configuration.AsEnumerable())
        {
            string key = pair.Key;
            if (ForbiddenSections.Any(section =>
                    key.Equals(section, StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith($"{section}:", StringComparison.OrdinalIgnoreCase))
                || key.StartsWith("Concurrency:User", StringComparison.OrdinalIgnoreCase))
            {
                validation.Invalid(key);
            }
        }
    }

    private static bool IsExactOrigin(string origin)
    {
        if (string.Equals(origin, "*", StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
            || (!string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal);
    }

    private static void ValidatePrivateEgressRules(Validation validation)
    {
        IConfigurationSection section = validation.Configuration.GetSection(
            PrivateEgressRulesKey);
        IConfigurationSection[] entries = [.. section.GetChildren()];
        if (section.Value is not null
            || entries.Length > MaximumPrivateEgressRules)
        {
            validation.Invalid(PrivateEgressRulesKey);
            return;
        }

        HashSet<int> indexes = [];
        HashSet<string> canonicalRules = new(StringComparer.Ordinal);
        foreach (IConfigurationSection entry in entries)
        {
            if (!TryParseCanonicalRuleIndex(entry.Key, out int index)
                || entry.Value is null
                || entry.GetChildren().Any()
                || !TryParsePrivateEgressRule(
                    entry.Value,
                    out string canonicalRule)
                || !indexes.Add(index)
                || !canonicalRules.Add(canonicalRule))
            {
                validation.Invalid(PrivateEgressRulesKey);
            }
        }

        if (indexes.Count != entries.Length
            || !indexes.Order().SequenceEqual(
                Enumerable.Range(0, indexes.Count)))
        {
            validation.Invalid(PrivateEgressRulesKey);
        }
    }

    private static bool TryParseCanonicalRuleIndex(
        string value,
        out int index) =>
        TryParseCanonicalArrayIndex(
            value,
            MaximumPrivateEgressRules,
            out index);

    private static bool TryParseCanonicalArrayIndex(
        string value,
        int maximumExclusive,
        out int index)
    {
        index = -1;
        return value.Length is >= 1 and <= 2
            && (value.Length == 1 || value[0] != '0')
            && value.All(static character => character is >= '0' and <= '9')
            && int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out index)
            && index >= 0
            && index < maximumExclusive;
    }

    private static bool TryParseCanonicalTrustedProxyCidr(
        string value,
        out string canonicalCidr)
    {
        canonicalCidr = string.Empty;
        if (value.Length is < 3 or > 64
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        int separator = value.LastIndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(0, separator).Contains('/')
            || !TryParseCanonicalDecimal(
                value[(separator + 1)..],
                out int prefixLength)
            || prefixLength == 0
            || !TryParseCanonicalTrustedProxyAddress(
                value[..separator],
                out IPAddress? address))
        {
            return false;
        }

        IPAddress parsedAddress = address!;
        int maximumPrefix = parsedAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => -1,
        };
        if (prefixLength > maximumPrefix)
        {
            return false;
        }

        byte[] networkBytes = parsedAddress.GetAddressBytes();
        ClearHostBits(networkBytes, prefixLength);
        IPAddress network = new(networkBytes);
        canonicalCidr = string.Create(
            CultureInfo.InvariantCulture,
            $"{network.ToString().ToLowerInvariant()}/{prefixLength}");
        return string.Equals(
            value,
            canonicalCidr,
            StringComparison.Ordinal);
    }

    private static bool TryParseCanonicalTrustedProxyAddress(
        string value,
        out IPAddress? address)
    {
        address = null;
        if (!value.Contains(':', StringComparison.Ordinal))
        {
            return TryParseCanonicalIpv4(value.AsSpan(), out address);
        }

        int dottedTailSeparator = value.LastIndexOf(':');
        if (value.Contains('.', StringComparison.Ordinal)
            && (dottedTailSeparator < 0
                || !TryParseCanonicalIpv4(
                    value.AsSpan(dottedTailSeparator + 1),
                    out _)))
        {
            return false;
        }

        if (!IPAddress.TryParse(value, out IPAddress? parsed)
            || parsed.AddressFamily != AddressFamily.InterNetworkV6
            || parsed.IsIPv4MappedToIPv6
            || !string.Equals(
                value,
                parsed.ToString().ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryParseCanonicalIpv4(
        ReadOnlySpan<char> value,
        out IPAddress? address)
    {
        address = null;
        Span<byte> bytes = stackalloc byte[4];
        int segmentIndex = 0;
        int segmentStart = 0;
        for (int index = 0; index <= value.Length; index++)
        {
            if (index != value.Length && value[index] != '.')
            {
                continue;
            }

            if (segmentIndex >= bytes.Length
                || !TryParseCanonicalDecimal(
                    value[segmentStart..index].ToString(),
                    out int segment)
                || segment > byte.MaxValue)
            {
                return false;
            }

            bytes[segmentIndex++] = checked((byte)segment);
            segmentStart = index + 1;
        }

        if (segmentIndex != bytes.Length)
        {
            return false;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool TryParsePrivateEgressRule(
        string value,
        out string canonicalRule)
    {
        canonicalRule = string.Empty;
        if (value.Length is < 1 or > 512
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character =>
                !char.IsAscii(character)
                || char.IsWhiteSpace(character)
                || character is '\\' or '\0'))
        {
            return false;
        }

        int separator = value.IndexOf('|');
        if (separator <= 0
            || separator != value.LastIndexOf('|')
            || separator == value.Length - 1
            || !TryParsePrivateEgressAuthority(
                value[..separator],
                out string canonicalAuthority)
            || !TryParsePrivateEgressCidr(
                value[(separator + 1)..],
                out string canonicalCidr))
        {
            return false;
        }

        canonicalRule = $"{canonicalAuthority}|{canonicalCidr}";
        return true;
    }

    private static bool TryParsePrivateEgressAuthority(
        string value,
        out string canonicalAuthority)
    {
        canonicalAuthority = string.Empty;
        if (!value.StartsWith("https://", StringComparison.Ordinal)
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal)
            || uri.HostNameType is UriHostNameType.Unknown
                or UriHostNameType.Basic
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string host = NormalizeEgressHost(uri);
        if (host.Length == 0
            || string.Equals(host, "localhost", StringComparison.Ordinal)
            || IPAddress.TryParse(host, out IPAddress? literal)
                && IPAddress.IsLoopback(CanonicalAddress(literal)))
        {
            return false;
        }

        string authorityHost = uri.HostNameType == UriHostNameType.IPv6
            ? $"[{host}]"
            : host;
        canonicalAuthority = string.Create(
            CultureInfo.InvariantCulture,
            $"https://{authorityHost}:{uri.Port}");
        return true;
    }

    private static string NormalizeEgressHost(Uri uri)
    {
        string host = uri.HostNameType == UriHostNameType.Dns
            ? uri.IdnHost
            : uri.DnsSafeHost;
        if (host.Length >= 2
            && host[0] == '['
            && host[^1] == ']')
        {
            host = host[1..^1];
        }

        return host.ToLowerInvariant();
    }

    private static bool TryParsePrivateEgressCidr(
        string value,
        out string canonicalCidr)
    {
        canonicalCidr = string.Empty;
        int separator = value.LastIndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(0, separator).Contains('/')
            || !TryParseCanonicalDecimal(
                value[(separator + 1)..],
                out int prefixLength)
            || value[..separator].Contains('%', StringComparison.Ordinal)
            || !IPAddress.TryParse(
                value[..separator],
                out IPAddress? parsed)
            || parsed.IsIPv4MappedToIPv6
            || parsed.AddressFamily == AddressFamily.InterNetwork
                && prefixLength > 32
            || parsed.AddressFamily == AddressFamily.InterNetworkV6
                && prefixLength > 128)
        {
            return false;
        }

        byte[] networkBytes = parsed.GetAddressBytes();
        ClearHostBits(networkBytes, prefixLength);
        IPAddress network = new(networkBytes);
        canonicalCidr = string.Create(
            CultureInfo.InvariantCulture,
            $"{network.ToString().ToLowerInvariant()}/{prefixLength}");
        return string.Equals(
                value,
                canonicalCidr,
                StringComparison.Ordinal)
            && IsPrivateNetwork(network, prefixLength);
    }

    private static bool TryParseCanonicalDecimal(
        string value,
        out int parsed)
    {
        parsed = 0;
        if (value.Length == 0
            || value.Length > 3
            || value.Length > 1 && value[0] == '0')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }

            parsed = checked((parsed * 10) + (character - '0'));
        }

        return true;
    }

    private static bool IsPrivateNetwork(
        IPAddress network,
        int prefixLength)
    {
        byte[] bytes = network.GetAddressBytes();
        if (network.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 && prefixLength >= 8
                || bytes is [172, >= 16 and <= 31, _, _]
                    && prefixLength >= 12
                || bytes is [192, 168, _, _]
                    && prefixLength >= 16;
        }

        return network.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xfe) == 0xfc
            && prefixLength >= 7;
    }

    private static IPAddress CanonicalAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6
            ? address.MapToIPv4()
            : address;

    private static void ClearHostBits(
        Span<byte> address,
        int prefixLength)
    {
        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits != 0)
        {
            byte mask = checked(
                (byte)(256 - (1 << (8 - remainingBits))));
            address[wholeBytes] &= mask;
            wholeBytes++;
        }

        address[wholeBytes..].Clear();
    }

    private static bool IsHostNameOrIpLiteral(string value)
    {
        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        if (value.Any(static character =>
                char.IsWhiteSpace(character)
                || character is ':' or '/' or '\\' or '@' or '?' or '#' or '[' or ']'))
        {
            return false;
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    private static bool IsSupportedMailbox(string value)
    {
        if (value.Length > 320
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(static character => character is '\r' or '\n' or '\0'
                || char.IsControl(character)))
        {
            return false;
        }

        MailAddress address;
        try
        {
            address = new MailAddress(value);
        }
        catch (FormatException)
        {
            return false;
        }

        int separator = address.Address.LastIndexOf('@');
        return string.Equals(address.Address, value, StringComparison.Ordinal)
            && separator > 0
            && separator < address.Address.Length - 1
            && IsSupportedMailboxLocalPart(address.Address[..separator])
            && TryNormalizeMailboxDomain(
                address.Address[(separator + 1)..],
                out string asciiDomain)
            && separator + 1 + asciiDomain.Length <= 254;
    }

    private static bool IsSupportedMailboxLocalPart(string localPart) =>
        localPart.Length is >= 1 and <= 64
            && localPart[0] != '.'
            && localPart[^1] != '.'
            && !localPart.Contains("..", StringComparison.Ordinal)
            && localPart.All(static character =>
                char.IsAsciiLetterOrDigit(character)
                    || character is '.' or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
                        or '+' or '-' or '/' or '=' or '?' or '^' or '_' or '`' or '{'
                        or '|' or '}' or '~');

    private static bool TryNormalizeMailboxDomain(string domain, out string asciiDomain)
    {
        try
        {
            asciiDomain = new IdnMapping
            {
                UseStd3AsciiRules = true,
            }.GetAscii(domain).ToLowerInvariant();
            return IsCanonicalDnsDomain(asciiDomain);
        }
        catch (ArgumentException)
        {
            asciiDomain = string.Empty;
            return false;
        }
    }

    private static bool IsCanonicalDnsDomain(string domain) =>
        domain.Length is >= 1 and <= 253
            && domain[0] != '.'
            && domain[^1] != '.'
            && domain.Split('.').All(static label =>
                label.Length is >= 1 and <= 63
                    && char.IsAsciiLetterOrDigit(label[0])
                    && char.IsAsciiLetterOrDigit(label[^1])
                    && label.All(static character =>
                        char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static bool IsRedisKeyPrefix(string value)
    {
        const string Prefix = "poolai:r1:";
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)
            || !value.EndsWith(':')
            || value.Length <= Prefix.Length + 1)
        {
            return false;
        }

        ReadOnlySpan<char> environment = value.AsSpan(
            Prefix.Length,
            value.Length - Prefix.Length - 1);
        if (environment.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (char character in environment)
        {
            if (character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Validation(IConfiguration configuration)
    {
        private readonly HashSet<string> invalidKeys = new(StringComparer.Ordinal);

        public IConfiguration Configuration { get; } = configuration;

        public void Invalid(string key) => invalidKeys.Add(key);

        public string Required(string key)
        {
            string? value = Configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                Invalid(key);
                return string.Empty;
            }

            return value;
        }

        public string String(string key, string defaultValue) =>
            string.IsNullOrWhiteSpace(Configuration[key]) ? defaultValue : Configuration[key]!;

        public string? Optional(string key) => Configuration[key];

        public string[] StringArray(string key, bool required = true)
        {
            string[] values = Configuration.GetSection(key).Get<string[]>() ?? [];
            if (required && values.Length == 0)
            {
                Invalid(key);
            }

            return values;
        }

        public void Length(string key, string defaultValue, int minimum, int maximum)
        {
            string value = String(key, defaultValue);
            if (value.Length < minimum || value.Length > maximum)
            {
                Invalid(key);
            }
        }

        public int Fixed(string key, int expected)
        {
            int value = Int(key, expected);
            if (value != expected)
            {
                Invalid(key);
            }

            return value;
        }

        public void FixedDecimal(string key, decimal expected)
        {
            decimal value = RangeDecimal(key, expected, expected, expected);
            if (value != expected)
            {
                Invalid(key);
            }
        }

        public void FixedLong(string key, long expected)
        {
            long value = Long(key, expected);
            if (value != expected)
            {
                Invalid(key);
            }
        }

        public int Range(string key, int defaultValue, int minimum, int maximum)
        {
            int value = Int(key, defaultValue);
            if (value < minimum || value > maximum)
            {
                Invalid(key);
            }

            return value;
        }

        public bool Boolean(string key, bool defaultValue)
        {
            string? configured = Configuration[key];
            if (configured is null)
            {
                return defaultValue;
            }

            if (!bool.TryParse(configured, out bool value))
            {
                Invalid(key);
                return defaultValue;
            }

            return value;
        }

        public long RangeLong(string key, long defaultValue, long minimum, long maximum)
        {
            long value = Long(key, defaultValue);
            if (value < minimum || value > maximum)
            {
                Invalid(key);
            }

            return value;
        }

        public decimal RangeDecimal(
            string key,
            decimal defaultValue,
            decimal minimum,
            decimal maximum)
        {
            string? configured = Configuration[key];
            decimal value = defaultValue;
            if (configured is not null
                && !decimal.TryParse(
                    configured,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                Invalid(key);
                return defaultValue;
            }

            if (value < minimum || value > maximum)
            {
                Invalid(key);
            }

            return value;
        }

        public void Base64Secret(string key, int minimumBytes)
        {
            string value = Required(key);
            if (!string.IsNullOrWhiteSpace(value) && !IsBase64AtLeast(value, minimumBytes))
            {
                Invalid(key);
            }
        }

        public void Base64SecretExact(string key, int exactBytes)
        {
            string value = Required(key);
            if (!string.IsNullOrWhiteSpace(value) && !IsBase64Exact(value, exactBytes))
            {
                Invalid(key);
            }
        }

        public void OptionalBase64Secret(string key, int minimumBytes)
        {
            string? value = Optional(key);
            if (!string.IsNullOrWhiteSpace(value) && !IsBase64AtLeast(value, minimumBytes))
            {
                Invalid(key);
            }
        }

        public void RequireEqualBase64Secrets(string leftKey, string rightKey)
        {
            string? leftEncoded = Optional(leftKey);
            string? rightEncoded = Optional(rightKey);
            if (string.IsNullOrWhiteSpace(leftEncoded)
                || string.IsNullOrWhiteSpace(rightEncoded))
            {
                return;
            }

            byte[]? left = null;
            byte[]? right = null;
            try
            {
                left = Convert.FromBase64String(leftEncoded);
                right = Convert.FromBase64String(rightEncoded);
                if (!CryptographicOperations.FixedTimeEquals(left, right))
                {
                    Invalid(leftKey);
                    Invalid(rightKey);
                }
            }
            catch (FormatException)
            {
                // Base64 shape is reported by Base64Secret; avoid duplicating
                // parsing errors here while still aggregating all invalid keys.
            }
            finally
            {
                Clear(left);
                Clear(right);
            }
        }

        public void RequireDistinctBase64Secrets(params string[] keys)
        {
            List<(string Key, byte[] Value)> values = [];
            foreach (string key in keys)
            {
                string? encoded = Optional(key);
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    continue;
                }

                try
                {
                    values.Add((key, Convert.FromBase64String(encoded)));
                }
                catch (FormatException)
                {
                    continue;
                }
            }

            try
            {
                for (int left = 0; left < values.Count; left++)
                {
                    for (int right = left + 1; right < values.Count; right++)
                    {
                        if (CryptographicOperations.FixedTimeEquals(
                                values[left].Value,
                                values[right].Value))
                        {
                            Invalid(values[left].Key);
                            Invalid(values[right].Key);
                        }
                    }
                }
            }
            finally
            {
                foreach ((_, byte[] value) in values)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
            }
        }

        public void ThrowIfInvalid()
        {
            if (invalidKeys.Count != 0)
            {
                throw new PoolAiConfigurationException(invalidKeys.Order(StringComparer.Ordinal).ToArray());
            }
        }

        private int Int(string key, int defaultValue)
        {
            string? configured = Configuration[key];
            if (configured is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                Invalid(key);
                return defaultValue;
            }

            return value;
        }

        private long Long(string key, long defaultValue)
        {
            string? configured = Configuration[key];
            if (configured is null)
            {
                return defaultValue;
            }

            if (!long.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                Invalid(key);
                return defaultValue;
            }

            return value;
        }

        private static bool IsBase64AtLeast(string value, int minimumBytes)
        {
            byte[]? decoded = null;
            try
            {
                decoded = Convert.FromBase64String(value);
                return decoded.Length >= minimumBytes;
            }
            catch (FormatException)
            {
                return false;
            }
            finally
            {
                Clear(decoded);
            }
        }

        private static bool IsBase64Exact(string value, int exactBytes)
        {
            byte[]? decoded = null;
            try
            {
                decoded = Convert.FromBase64String(value);
                return decoded.Length == exactBytes;
            }
            catch (FormatException)
            {
                return false;
            }
            finally
            {
                Clear(decoded);
            }
        }

        private static void Clear(byte[]? value)
        {
            if (value is not null)
            {
                CryptographicOperations.ZeroMemory(value);
            }
        }
    }
}
